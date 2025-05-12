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

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            _logger.LogInformation("New MP4 file detected: {File}", e.Name);

            // Wait for file to be completely written
            WaitForFile(e.FullPath);

            string outputFile = Path.Combine(
                _watchFolder,
                Path.GetFileNameWithoutExtension(e.Name) + ".wav"
            );

            ExtractAudio(e.FullPath, outputFile);

            _logger.LogInformation("Successfully extracted audio to: {OutputFile}", outputFile);

            // Transcribe with Vosk after audio extraction
            string transcriptFile = Path.Combine(
                _watchFolder,
                Path.GetFileNameWithoutExtension(e.Name) + ".txt"
            );
            TranscribeWavWithVosk(outputFile, transcriptFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file: {File}", e.Name);
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
        _logger.LogInformation("Transcription saved to: {TranscriptFile}", transcriptFilePath);
    }
}
