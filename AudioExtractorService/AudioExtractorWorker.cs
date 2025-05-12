using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Vosk;
using NAudio.Wave;

namespace AudioExtractorService;

public class AudioExtractorWorker : BackgroundService
{
    private readonly ILogger<AudioExtractorWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly FileSystemWatcher _watcher;
    private readonly string _watchFolder;
    private readonly string _ffmpegPath;

    public AudioExtractorWorker(ILogger<AudioExtractorWorker> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _watchFolder = _configuration["WatchFolder"] ?? throw new InvalidOperationException("WatchFolder not configured");
        _ffmpegPath = _configuration["FFmpegPath"] ?? "ffmpeg.exe";

        // Ensure watch folder exists
        Directory.CreateDirectory(_watchFolder);

        _watcher = new FileSystemWatcher
        {
            Path = _watchFolder,
            Filter = "*.mp4",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _watcher.Created += OnFileCreated;
        _watcher.EnableRaisingEvents = true;

        return Task.CompletedTask;
    }

    private async void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        var perFileLog = new System.Text.StringBuilder();
        string logFile = Path.Combine(_watchFolder, Path.GetFileNameWithoutExtension(e.Name) + ".log");
        try
        {
            perFileLog.AppendLine($"[{DateTime.Now:O}] New MP4 file detected: {e.Name}");

            // 1. Wait for file
            var swWait = System.Diagnostics.Stopwatch.StartNew();
            WaitForFile(e.FullPath);
            swWait.Stop();
            perFileLog.AppendLine($"[{DateTime.Now:O}] Waited {swWait.ElapsedMilliseconds} ms for file to be ready: {e.Name}");

            // 2. Extract audio
            var swExtract = System.Diagnostics.Stopwatch.StartNew();
            string outputFile = Path.Combine(
                _watchFolder,
                Path.GetFileNameWithoutExtension(e.Name) + ".wav"
            );
            ExtractAudio(e.FullPath, outputFile);
            swExtract.Stop();
            perFileLog.AppendLine($"[{DateTime.Now:O}] Audio extraction completed in {swExtract.ElapsedMilliseconds} ms: {outputFile}");

            bool transcribeVosk = Convert.ToBoolean(_configuration["VoskEnabled"]);
            bool transcribeAzure = Convert.ToBoolean(_configuration["AzureEnabled"]);

            // 3. Vosk transcription
            if (transcribeVosk)
            {
                var swVosk = System.Diagnostics.Stopwatch.StartNew();
                string transcriptFileVosk = Path.Combine(
                    _watchFolder,
                    Path.GetFileNameWithoutExtension(e.Name) + ".vosk.txt"
                );
                TranscribeWavWithVosk(outputFile, transcriptFileVosk);
                swVosk.Stop();
                perFileLog.AppendLine($"[{DateTime.Now:O}] Vosk transcription completed in {swVosk.ElapsedMilliseconds} ms: {transcriptFileVosk}");
            }

            // 4. Azure transcription and VTT
            if (transcribeAzure)
            {
                var swAzure = System.Diagnostics.Stopwatch.StartNew();
                string transcriptFileAzure = Path.Combine(
                    _watchFolder,
                    Path.GetFileNameWithoutExtension(e.Name) + ".azure.txt"
                );
                string vttFileAzure = Path.Combine(
                    _watchFolder,
                    Path.GetFileNameWithoutExtension(e.Name) + ".azure.vtt"
                );
                await TranscribeAndGenerateVttWithAzureAsync(outputFile, transcriptFileAzure, vttFileAzure);
                swAzure.Stop();
                perFileLog.AppendLine($"[{DateTime.Now:O}] Azure transcription and VTT generation completed in {swAzure.ElapsedMilliseconds} ms: {transcriptFileAzure}, {vttFileAzure}");
            }
        }
        catch (Exception ex)
        {
            perFileLog.AppendLine($"[{DateTime.Now:O}] Error processing file: {e.Name} - {ex}");
        }
        finally
        {
            System.IO.File.WriteAllText(logFile, perFileLog.ToString());
        }
    }

    private void WaitForFile(string filePath)
    {
        const int maxAttempts = 10;
        int attempts = 0;
        while (attempts < maxAttempts)
        {
            try
            {
                using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    stream.Close();
                    return;
                }
            }
            catch (IOException)
            {
                attempts++;
                Thread.Sleep(1000); // Wait 1 second before trying again
            }
        }
        throw new TimeoutException($"File {filePath} is still being used after {maxAttempts} seconds");
    }

    private void ExtractAudio(string inputFile, string outputFile)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = $"-i \"{inputFile}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 \"{outputFile}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.Start();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new Exception($"FFmpeg failed with error: {error}");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        return base.StopAsync(cancellationToken);
    }

    private void TranscribeWavWithVosk(string wavFilePath, string transcriptFilePath)
    {
        // Path to your Vosk model directory (update as needed)
        string modelPath = _configuration["VoskModelPath"] ?? "models/vosk-model-small-en-us-0.15";

        if (!Directory.Exists(modelPath))
        {
            _logger.LogError("Vosk model directory not found: {ModelPath}", modelPath);
            File.WriteAllText(transcriptFilePath, "[Vosk model not found]");
            return;
        }

        Vosk.Vosk.SetLogLevel(0); // Optional: reduce Vosk logging

        using var model = new Model(modelPath);
        using var waveReader = new WaveFileReader(wavFilePath);

        if (waveReader.WaveFormat.Encoding != WaveFormatEncoding.Pcm ||
            waveReader.WaveFormat.BitsPerSample != 16 ||
            waveReader.WaveFormat.SampleRate != 16000 ||
            waveReader.WaveFormat.Channels != 1)
        {
            _logger.LogError("WAV file must be 16kHz, 16-bit, mono PCM.");
            File.WriteAllText(transcriptFilePath, "[Invalid WAV format for Vosk]");
            return;
        }

        using var rec = new VoskRecognizer(model, 16000.0f);
        int bytesRead;
        byte[] buffer = new byte[4096];
        while ((bytesRead = waveReader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (!rec.AcceptWaveform(buffer, bytesRead))
            {
                // Partial result, can be ignored or logged
            }
        }
        var result = rec.FinalResult();
        var text = System.Text.Json.JsonDocument.Parse(result).RootElement.GetProperty("text").GetString();
        File.WriteAllText(transcriptFilePath, text ?? "[No speech recognized]");
    }
  
  
    private async Task TranscribeAndGenerateVttWithAzureAsync(string wavFilePath, string transcriptFilePath, string vttFilePath)
    {
        string azureKey = _configuration["AzureSpeechKey"];
        string azureRegion = _configuration["AzureSpeechRegion"];

        if (string.IsNullOrEmpty(azureKey) || string.IsNullOrEmpty(azureRegion))
        {
            _logger.LogError("Azure Speech configuration missing.");
            await File.WriteAllTextAsync(transcriptFilePath, "[Azure Speech configuration missing]");
            await File.WriteAllTextAsync(vttFilePath, "WEBVTT\n\nNOTE Azure Speech configuration missing");
            return;
        }

        var config = Microsoft.CognitiveServices.Speech.SpeechConfig.FromSubscription(azureKey, azureRegion);
        config.SpeechRecognitionLanguage = "en-US";
        config.OutputFormat = Microsoft.CognitiveServices.Speech.OutputFormat.Detailed;

        using var audioInput = Microsoft.CognitiveServices.Speech.Audio.AudioConfig.FromWavFileInput(wavFilePath);
        using var recognizer = new Microsoft.CognitiveServices.Speech.SpeechRecognizer(config, audioInput);

        var transcriptLines = new List<string>();
        var vttBuilder = new System.Text.StringBuilder();
        vttBuilder.AppendLine("WEBVTT\n");
        int captionIndex = 1;

        var stopRecognition = new TaskCompletionSource<int>();

        recognizer.Recognized += (s, e) =>
        {
            if (e.Result.Reason == Microsoft.CognitiveServices.Speech.ResultReason.RecognizedSpeech)
            {
                // Add to transcript
                if (!string.IsNullOrWhiteSpace(e.Result.Text))
                    transcriptLines.Add(e.Result.Text);

                // Add to VTT
                var json = System.Text.Json.JsonDocument.Parse(
                    e.Result.Properties.GetProperty(Microsoft.CognitiveServices.Speech.PropertyId.SpeechServiceResponse_JsonResult)
                );
                if (json.RootElement.TryGetProperty("NBest", out var nbest) && nbest.GetArrayLength() > 0)
                {
                    var phrase = nbest[0].GetProperty("Display").GetString();
                    var words = nbest[0].GetProperty("Words");
                    if (words.GetArrayLength() > 0)
                    {
                        var firstWord = words[0];
                        var lastWord = words[words.GetArrayLength() - 1];

                        var start = TimeSpan.FromSeconds(firstWord.GetProperty("Offset").GetInt64() / 10000000.0);
                        var end = TimeSpan.FromSeconds(
                            (lastWord.GetProperty("Offset").GetInt64() + lastWord.GetProperty("Duration").GetInt64()) / 10000000.0
                        );

                        vttBuilder.AppendLine($"{captionIndex}");
                        vttBuilder.AppendLine($"{start:hh\\:mm\\:ss\\.fff} --> {end:hh\\:mm\\:ss\\.fff}");
                        vttBuilder.AppendLine(phrase);
                        vttBuilder.AppendLine();
                        captionIndex++;
                    }
                }
            }
        };

        recognizer.SessionStopped += (s, e) => stopRecognition.TrySetResult(0);
        recognizer.Canceled += (s, e) => stopRecognition.TrySetResult(0);

        await recognizer.StartContinuousRecognitionAsync();
        await stopRecognition.Task;
        await recognizer.StopContinuousRecognitionAsync();

        await File.WriteAllTextAsync(transcriptFilePath, string.Join(Environment.NewLine, transcriptLines));
        await File.WriteAllTextAsync(vttFilePath, vttBuilder.ToString());

        _logger.LogInformation("Azure transcription and VTT saved to: {TranscriptFile}, {VttFile}", transcriptFilePath, vttFilePath);
    }
}
