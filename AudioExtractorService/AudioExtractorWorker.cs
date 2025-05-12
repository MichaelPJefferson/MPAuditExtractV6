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
        string outputFile = Path.Combine(_watchFolder, Path.GetFileNameWithoutExtension(e.Name) + ".wav");
        string transcriptFileVosk = Path.Combine(_watchFolder, Path.GetFileNameWithoutExtension(e.Name) + ".vosk.txt");
        string transcriptFileAzure = Path.Combine(_watchFolder, Path.GetFileNameWithoutExtension(e.Name) + ".azure.txt");
        string vttFileAzure = Path.Combine(_watchFolder, Path.GetFileNameWithoutExtension(e.Name) + ".azure.vtt");
        var targetLanguages = _configuration.GetSection("AzureTranslatorLanguages").Get<string[]>();

        try
        {
            perFileLog.AppendLine($"[{DateTime.Now:O}] New MP4 file detected: {e.Name}");

            // 1. Wait for file
            try
            {
                var swWait = System.Diagnostics.Stopwatch.StartNew();
                WaitForFile(e.FullPath);
                swWait.Stop();
                perFileLog.AppendLine($"[{DateTime.Now:O}] Waited {swWait.ElapsedMilliseconds} ms for file to be ready: {e.Name}");
            }
            catch (Exception ex)
            {
                perFileLog.AppendLine($"[{DateTime.Now:O}] Error waiting for file: {ex}");
                return;
            }

            // 2. Extract audio
            try
            {
                var swExtract = System.Diagnostics.Stopwatch.StartNew();
                ExtractAudio(e.FullPath, outputFile);
                swExtract.Stop();
                perFileLog.AppendLine($"[{DateTime.Now:O}] Audio extraction completed in {swExtract.ElapsedMilliseconds} ms: {outputFile}");
            }
            catch (Exception ex)
            {
                perFileLog.AppendLine($"[{DateTime.Now:O}] Error extracting audio: {ex}");
                return;
            }

            // 3. Vosk transcription
            bool transcribeVosk = Convert.ToBoolean(_configuration["VoskEnabled"]);
            if (transcribeVosk)
            {
                try
                {
                    var swVosk = System.Diagnostics.Stopwatch.StartNew();
                    TranscribeWavWithVosk(outputFile, transcriptFileVosk);
                    swVosk.Stop();
                    perFileLog.AppendLine($"[{DateTime.Now:O}] Vosk transcription completed in {swVosk.ElapsedMilliseconds} ms: {transcriptFileVosk}");
                }
                catch (Exception ex)
                {
                    perFileLog.AppendLine($"[{DateTime.Now:O}] Error in Vosk transcription: {ex}");
                    // Continue to Azure even if Vosk fails
                }
            }

            // 4. Azure transcription and VTT (with translations)
            bool transcribeAzure = Convert.ToBoolean(_configuration["AzureEnabled"]);
            if (transcribeAzure)
            {
                try
                {
                    var swAzure = System.Diagnostics.Stopwatch.StartNew();
                    await TranscribeAndGenerateVttWithAzureAsync(outputFile, transcriptFileAzure, vttFileAzure, targetLanguages);
                    swAzure.Stop();
                    perFileLog.AppendLine($"[{DateTime.Now:O}] Azure transcription and VTT generation (with translations) completed in {swAzure.ElapsedMilliseconds} ms: {transcriptFileAzure}, {vttFileAzure}");
                }
                catch (Exception ex)
                {
                    perFileLog.AppendLine($"[{DateTime.Now:O}] Error in Azure transcription/VTT/translation: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            perFileLog.AppendLine($"[{DateTime.Now:O}] Unexpected error processing file: {e.Name} - {ex}");
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
        try
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
        catch (Exception ex)
        {
            throw new Exception($"Audio extraction failed for {inputFile}: {ex.Message}", ex);
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
  
  
async Task TranscribeAndGenerateVttWithAzureAsync(
        string wavFilePath,
        string transcriptFilePath,
        string vttFilePath,
        string[] targetLanguages = null)
    {
        string azureKey = _configuration["AzureSpeechKey"];
        string azureRegion = _configuration["AzureSpeechRegion"];

        if (string.IsNullOrEmpty(azureKey) || string.IsNullOrEmpty(azureRegion))
        {
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

        var vttSegments = new List<(string phrase, TimeSpan start, TimeSpan end)>();

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

                        vttSegments.Add((phrase, start, end));
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

        // Write original files
        await File.WriteAllTextAsync(transcriptFilePath, string.Join(Environment.NewLine, transcriptLines));
        await File.WriteAllTextAsync(vttFilePath, vttBuilder.ToString());

        // Translate to other languages if requested
        if (targetLanguages != null)
        {
            foreach (var lang in targetLanguages)
            {
                // Translate TXT
                string translatedTxtPath = Path.Combine(
                    Path.GetDirectoryName(transcriptFilePath)!,
                    Path.GetFileNameWithoutExtension(transcriptFilePath) + $".{lang}.txt"
                );
                await TranslateFileAsync(transcriptFilePath, translatedTxtPath, lang);

                // Translate VTT
                string translatedVttPath = Path.Combine(
                    Path.GetDirectoryName(vttFilePath)!,
                    Path.GetFileNameWithoutExtension(vttFilePath) + $".{lang}.vtt"
                );

                // Translate each phrase and build a new VTT
                var translatedVttBuilder = new System.Text.StringBuilder();
                translatedVttBuilder.AppendLine("WEBVTT\n");
                int idx = 1;
                foreach (var (phrase, start, end) in vttSegments)
                {
                    string translatedPhrase = await TranslateTextAsync(phrase, lang);
                    translatedVttBuilder.AppendLine($"{idx}");
                    translatedVttBuilder.AppendLine($"{start:hh\\:mm\\:ss\\.fff} --> {end:hh\\:mm\\:ss\\.fff}");
                    translatedVttBuilder.AppendLine(translatedPhrase);
                    translatedVttBuilder.AppendLine();
                    idx++;
                }
                await File.WriteAllTextAsync(translatedVttPath, translatedVttBuilder.ToString());
            }
        }
    }

    private async Task TranslateFileAsync(string inputFile, string outputFile, string targetLanguage)
    {
        string subscriptionKey = _configuration["AzureTranslatorKey"];
        string endpoint = _configuration["AzureTranslatorEndpoint"];

        if (string.IsNullOrEmpty(subscriptionKey) || string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException("Azure Translator configuration missing.");
        }

        string text = await File.ReadAllTextAsync(inputFile);

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
            client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Region", _configuration["AzureTranslatorRegion"] ?? "global");

            var requestBody = System.Text.Json.JsonSerializer.Serialize(new[] { new { Text = text } });
            var requestContent = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

            string route = $"/translate?api-version=3.0&to={targetLanguage}";
            var response = await client.PostAsync(endpoint.TrimEnd('/') + route, requestContent);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Translation API failed: {response.StatusCode} - {errorContent}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(jsonResponse);
            var translatedText = doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString();

            await File.WriteAllTextAsync(outputFile, translatedText ?? "");
        }
        catch (Exception ex)
        {
            throw new Exception($"Translation failed for {inputFile} to {targetLanguage}: {ex.Message}", ex);
        }
    }
    private async Task<string> TranslateTextAsync(string text, string targetLanguage)
    {
        string subscriptionKey = _configuration["AzureTranslatorKey"];
        string endpoint = _configuration["AzureTranslatorEndpoint"];

        if (string.IsNullOrEmpty(subscriptionKey) || string.IsNullOrEmpty(endpoint))
            return text;

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
        client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Region", _configuration["AzureTranslatorRegion"] ?? "global");

        var requestBody = System.Text.Json.JsonSerializer.Serialize(new[] { new { Text = text } });
        var requestContent = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

        string route = $"/translate?api-version=3.0&to={targetLanguage}";
        var response = await client.PostAsync(endpoint.TrimEnd('/') + route, requestContent);
        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(jsonResponse);
        return doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? text;
    }
}
