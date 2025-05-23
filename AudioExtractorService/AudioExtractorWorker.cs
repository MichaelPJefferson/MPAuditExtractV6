using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Vosk;
using NAudio.Wave;

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace AudioExtractorService;

/// <summary>
/// Background service that watches a folder for new MP4 files, extracts audio, transcribes, translates, and generates VTT/WAV files.
/// </summary>
public class AudioExtractorWorker : BackgroundService
{
    private readonly ILogger<AudioExtractorWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly FileSystemWatcher _watcher;
    private readonly string _watchFolder;
    private readonly string _ffmpegPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioExtractorWorker"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="configuration">Application configuration.</param>
    public AudioExtractorWorker(ILogger<AudioExtractorWorker> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _watchFolder = _configuration["WatchFolder"] ?? throw new InvalidOperationException("WatchFolder not configured");
        _ffmpegPath = _configuration["FFmpegPath"] ?? "ffmpeg.exe";
        Console.WriteLine($"WatchFolder {_watchFolder}");

        // Ensure watch folder exists
        Directory.CreateDirectory(_watchFolder);

        _watcher = new FileSystemWatcher
        {
            Path = _watchFolder,
            Filter = "*.mp4",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
    }

    /// <summary>
    /// Starts the background service and begins watching for new files.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _watcher.Created += OnFileCreated;
        _watcher.EnableRaisingEvents = true;

        // Process existing files at startup
        var existingFiles = Directory.GetFiles(_watchFolder, "*.mp4")
                .Where(f => !Path.GetFileName(f).Contains("azure", StringComparison.OrdinalIgnoreCase));

        foreach (var file in existingFiles)
        {
            // Optionally check for cancellation
            if (stoppingToken.IsCancellationRequested)
                break;

            // Process each file asynchronously, but don't await all at once to avoid overload
            await ProcessFileAsync(file);
        }

        // Keep the service running
        await Task.CompletedTask;
    }


    /// <summary>
    /// Handles the creation of new files in the watched folder.
    /// </summary>
    private async void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        // Exclude files with "azure" in the name (case-insensitive)
        if (e.Name != null && e.Name.Contains(".azure.", StringComparison.OrdinalIgnoreCase))
            return;

        await ProcessFileInternalAsync(e);
    }

    /// <summary>
    /// Waits until the specified file is no longer locked by another process.
    /// </summary>
    /// <param name="filePath">The file path to check.</param>
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


    /// <summary>
    /// Extracts audio from an input video file using FFmpeg.
    /// </summary>
    /// <param name="inputFile">The input video file path.</param>
    /// <param name="outputFile">The output WAV file path.</param>
    private void ExtractAudio(string inputFile, string outputFile)
    {
        Console.WriteLine($"[FFmpeg] Preparing to extract audio from: {inputFile}");
        string arguments = $"-i \"{inputFile}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 \"{outputFile}\"";
        Console.WriteLine($"[FFmpeg] Command: {_ffmpegPath} {arguments}");

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            bool started = process.Start();
            if (!started)
            {
                Console.WriteLine("[FFmpeg] Failed to start FFmpeg process.");
                throw new Exception("FFmpeg process could not be started.");
            }

            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"[FFmpeg] FFmpeg exited with code {process.ExitCode}. Error output:\n{error}");
                throw new Exception($"FFmpeg failed with error: {error}");
            }

            Console.WriteLine($"[FFmpeg] Audio extraction succeeded: {outputFile}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FFmpeg] Exception during audio extraction: {ex.Message}");
            throw new Exception($"Audio extraction failed for {inputFile}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Stops the background service and disposes the file system watcher.
    /// </summary>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Transcribes a WAV file using the Vosk speech recognition engine.
    /// </summary>
    /// <param name="wavFilePath">Path to the WAV file.</param>
    /// <param name="transcriptFilePath">Path to save the transcript.</param>
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


    /// <summary>
    /// Transcribes a WAV audio file using Azure Speech-to-Text, generates a VTT subtitle file,
    /// writes the transcript and VTT to disk, and for each target language, translates the transcript and VTT,
    /// synthesizes speech, and muxes the new audio with the original video.
    /// </summary>
    /// <param name="sourceVideoPath">Path to the original source video file (MP4).</param>
    /// <param name="wavFilePath">Path to the extracted WAV audio file.</param>
    /// <param name="transcriptFilePath">Path to save the Azure transcript text file.</param>
    /// <param name="vttFilePath">Path to save the Azure-generated VTT subtitle file.</param>
    /// <param name="targetLanguages">Array of target language codes for translation and synthesis (e.g., "es", "fr").</param>
    /// <param name="perFileLog">StringBuilder for logging per-file processing details and errors.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task TranscribeAndGenerateVttWithAzureAsync(
    string sourceVideoPath,
    string wavFilePath,
    string transcriptFilePath,
    string vttFilePath,
    string[] targetLanguages,
    System.Text.StringBuilder perFileLog)
    {
        perFileLog.AppendLine($"[{DateTime.Now:O}] [Azure] Starting transcription for: {wavFilePath}");

        var (transcriptLines, vttSegments, vttContent) = await AzureTranscribeWav(wavFilePath, perFileLog);
        if (transcriptLines == null || vttSegments == null)
            return;

        await WriteTranscriptAndVtt(transcriptFilePath, vttFilePath, transcriptLines, vttContent, perFileLog);

        if (targetLanguages != null)
        {
            foreach (var lang in targetLanguages)
            {
                try
                {
                    await ProcessAzureLanguageVariant(
                        sourceVideoPath, transcriptFilePath, vttFilePath, vttSegments, lang, perFileLog);
                }
                catch (Exception ex)
                {
                    perFileLog.AppendLine($"[{DateTime.Now:O}] [Azure] Error in translation/synthesis for {lang}: {ex}");
                }
            }
        }
    }


    /// <summary>
    /// Performs detailed speech recognition on a WAV audio file using Azure Speech-to-Text,
    /// returning the recognized transcript lines, VTT (WebVTT) caption segments, and the full VTT content.
    /// </summary>
    /// <param name="wavFilePath">Path to the input WAV audio file.</param>
    /// <param name="perFileLog">StringBuilder for logging per-file processing details and errors.</param>
    /// <returns>
    /// A tuple containing:
    ///   - List of recognized transcript lines,
    ///   - List of VTT segments (phrase, start time, end time),
    ///   - The complete VTT file content as a string.
    /// Returns (null, null, string.Empty) if recognition fails or configuration is missing.
    /// </returns>
    private async Task<(List<string>? transcriptLines, List<(string phrase, TimeSpan start, TimeSpan end)>? vttSegments, string vttContent)>
        AzureTranscribeWav(string wavFilePath, System.Text.StringBuilder perFileLog)
    {
        string azureKey = _configuration["AzureSpeechKey"];
        string azureRegion = _configuration["AzureSpeechRegion"];

        if (string.IsNullOrEmpty(azureKey) || string.IsNullOrEmpty(azureRegion))
        {
            perFileLog.AppendLine($"[{DateTime.Now:O}] [Azure] Speech configuration missing.");
            return (null, null, string.Empty);
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
                if (!string.IsNullOrWhiteSpace(e.Result.Text))
                    transcriptLines.Add(e.Result.Text);

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

        perFileLog.AppendLine($"[{DateTime.Now:O}] [Azure] Starting speech recognition...");
        recognizer.SessionStopped += (s, e) => stopRecognition.TrySetResult(0);
        recognizer.Canceled += (s, e) => stopRecognition.TrySetResult(0);

        await recognizer.StartContinuousRecognitionAsync();
        await stopRecognition.Task;
        await recognizer.StopContinuousRecognitionAsync();
        perFileLog.AppendLine($"[{DateTime.Now:O}] [Azure] Speech recognition complete.");

        return (transcriptLines, vttSegments, vttBuilder.ToString());
    }



    /// <summary>
    /// Writes the transcript lines to a text file and the VTT (WebVTT) content to a subtitle file.
    /// Logs the completion of these actions and verifies that the files were created successfully.
    /// </summary>
    /// <param name="transcriptFilePath">Path to save the transcript text file.</param>
    /// <param name="vttFilePath">Path to save the VTT subtitle file.</param>
    /// <param name="transcriptLines">List of transcript lines to write.</param>
    /// <param name="vttContent">Full VTT file content as a string.</param>
    /// <param name="perFileLog">StringBuilder for logging per-file processing details and errors.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task WriteTranscriptAndVtt(
        string transcriptFilePath,
        string vttFilePath,
        List<string> transcriptLines,
        string vttContent,
        System.Text.StringBuilder perFileLog)
    {
        await File.WriteAllTextAsync(transcriptFilePath, string.Join(Environment.NewLine, transcriptLines));
        await File.WriteAllTextAsync(vttFilePath, vttContent);
        perFileLog.AppendLine($"[{DateTime.Now:O}] [Azure] Transcript and VTT files written: {transcriptFilePath}, {vttFilePath}");

        if (!File.Exists(transcriptFilePath))
            perFileLog.AppendLine($"[{DateTime.Now:O}] ERROR: Expected Azure transcript not found: {transcriptFilePath}");
        if (!File.Exists(vttFilePath))
            perFileLog.AppendLine($"[{DateTime.Now:O}] ERROR: Expected Azure VTT not found: {vttFilePath}");
    }


    /// <summary>
    /// For a given target language, translates the transcript and VTT files, writes the translated files,
    /// synthesizes speech from the translated VTT using Azure Speech, and muxes the synthesized audio with the original video.
    /// Logs each step and verifies the existence of all expected output files.
    /// </summary>
    /// <param name="sourceVideoPath">Path to the original source video file (MP4).</param>
    /// <param name="transcriptFilePath">Path to the transcript text file to translate.</param>
    /// <param name="vttFilePath">Path to the VTT subtitle file to translate.</param>
    /// <param name="vttSegments">List of VTT segments (phrase, start time, end time) for translation and synthesis.</param>
    /// <param name="lang">Target language code (e.g., "es", "fr").</param>
    /// <param name="perFileLog">StringBuilder for logging per-file processing details and errors.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ProcessAzureLanguageVariant(
        string sourceVideoPath,
        string transcriptFilePath,
        string vttFilePath,
        List<(string phrase, TimeSpan start, TimeSpan end)> vttSegments,
        string lang,
        System.Text.StringBuilder perFileLog)
    {
        perFileLog.AppendLine($"[{DateTime.Now:O}] [Azure] Translating to {lang}...");

        string translatedTxtPath = Path.Combine(
            Path.GetDirectoryName(transcriptFilePath)!,
            Path.GetFileNameWithoutExtension(transcriptFilePath) + $".{lang}.txt"
        );
        await TranslateFileAsync(transcriptFilePath, translatedTxtPath, lang);

        string translatedVttPath = Path.Combine(
            Path.GetDirectoryName(vttFilePath)!,
            Path.GetFileNameWithoutExtension(vttFilePath) + $".{lang}.vtt"
        );
        await WriteTranslatedVtt(translatedVttPath, vttSegments, lang, perFileLog);

        if (!File.Exists(translatedTxtPath))
        {
            perFileLog.AppendLine($"[{DateTime.Now:O}] ERROR: Expected translated TXT not found: {translatedTxtPath}");
            return;
        }
        if (!File.Exists(translatedVttPath))
        {
            perFileLog.AppendLine($"[{DateTime.Now:O}] ERROR: Expected translated VTT not found: {translatedVttPath}");
            return;
        }

        string translatedWavPath = Path.Combine(
            Path.GetDirectoryName(transcriptFilePath)!,
            Path.GetFileNameWithoutExtension(transcriptFilePath) + $".{lang}.wav"
        );
        await GenerateWavFromVttAsync(
            translatedVttPath,
            translatedWavPath,
            _configuration["AzureSpeechKey"],
            _configuration["AzureSpeechRegion"],
            lang,
            GetVoiceNameForLanguage(lang),
            perFileLog
        );
        perFileLog.AppendLine($"[{DateTime.Now:O}] [Azure] Synthesized WAV for {lang}: {translatedWavPath}");

        if (!File.Exists(translatedWavPath))
        {
            perFileLog.AppendLine($"[{DateTime.Now:O}] ERROR: Expected synthesized WAV not found: {translatedWavPath}");
            return;
        }

        string outputMp4Path = Path.Combine(
            Path.GetDirectoryName(translatedWavPath)!,
            Path.GetFileNameWithoutExtension(translatedWavPath) + ".mp4"
        );
        await MuxVideoWithWavAsync(sourceVideoPath, translatedWavPath, outputMp4Path);
        perFileLog.AppendLine($"[{DateTime.Now:O}] Muxed video and audio to {outputMp4Path}");

        if (!File.Exists(outputMp4Path))
            perFileLog.AppendLine($"[{DateTime.Now:O}] ERROR: Expected muxed MP4 not found: {outputMp4Path}");
    }


    /// <summary>
    /// Translates each phrase in the provided VTT segments to the specified target language,
    /// constructs a translated VTT (WebVTT) subtitle file, writes it to disk, and logs the operation.
    /// </summary>
    /// <param name="translatedVttPath">Path to save the translated VTT subtitle file.</param>
    /// <param name="vttSegments">List of VTT segments (phrase, start time, end time) to translate and write.</param>
    /// <param name="lang">Target language code for translation (e.g., "es", "fr").</param>
    /// <param name="perFileLog">StringBuilder for logging per-file processing details and errors.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task WriteTranslatedVtt(
        string translatedVttPath,
        List<(string phrase, TimeSpan start, TimeSpan end)> vttSegments,
        string lang,
        System.Text.StringBuilder perFileLog)
    {
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
        perFileLog.AppendLine($"[{DateTime.Now:O}] [Azure] Translation and VTT for {lang} written: {translatedVttPath}");
    }

    /// <summary>
    /// Maps a language code to an Azure voice name.
    /// </summary>
    /// <param name="lang">Language code (e.g., "es").</param>
    /// <returns>Azure voice name.</returns>
    private string GetVoiceNameForLanguage(string lang)
    {
        var voices = _configuration.GetSection("AzureVoiceNames").Get<Dictionary<string, string>>();
        return voices != null && voices.TryGetValue(lang, out var voice)
            ? voice
            : voices?["default"] ?? "en-US-GuyNeural";
    }

    /// <summary>
    /// Maps a language code to an Azure voice name.
    /// </summary>
    /// <param name="lang">Language code (e.g., "es").</param>
    /// <returns>Azure voice name.</returns>
    private string GetFolderForLanguage(string lang)
    {
        var folders = _configuration.GetSection("LanguageFolders").Get<Dictionary<string, string>>();
        return folders != null && folders.TryGetValue(lang, out var folder)
            ? folder
            : folders?["default"] ?? "English";
    }
    /// <summary>
    /// Translates a text file to a target language using Azure Translator and saves the result.
    /// </summary>
    /// <param name="inputFile">Input file path.</param>
    /// <param name="outputFile">Output file path.</param>
    /// <param name="targetLanguage">Target language code.</param>
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
    
    /// <summary>
    /// Translates a string to a target language using Azure Translator.
    /// </summary>
    /// <param name="text">Text to translate.</param>
    /// <param name="targetLanguage">Target language code.</param>
    /// <returns>Translated text.</returns>
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
        /// <summary>
    /// Generates a WAV file from a VTT file by synthesizing each cue using Azure Speech.
    /// </summary>
    /// <param name="vttFilePath">Input VTT file path.</param>
    /// <param name="outputWavPath">Output WAV file path.</param>
    /// <param name="azureSpeechKey">Azure Speech API key.</param>
    /// <param name="azureSpeechRegion">Azure Speech region.</param>
    /// <param name="languageCode">Language code for synthesis (e.g., "es-ES").</param>
    /// <param name="voiceName">Azure voice name for synthesis.</param>

    public async Task GenerateWavFromVttAsync(
        string vttFilePath,
        string outputWavPath,
        string azureSpeechKey,
        string azureSpeechRegion,
        string languageCode, // e.g., "es-ES"
        string voiceName,     // e.g., "es-ES-AlvaroNeural"
        System.Text.StringBuilder perFileLog)
    {
        // 1. Parse VTT
        var cues = new List<(TimeSpan start, TimeSpan end, string text)>();
        var regex = new Regex(
            @"^\s*(\d+)\s*[\r\n]+(\d{2}:\d{2}:\d{2}\.\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}\.\d{3})\s*[\r\n]+(.*?)(?=(?:\r?\n){2,}|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);
        var vttText = await File.ReadAllTextAsync(vttFilePath);
        foreach (Match match in regex.Matches(vttText))
        {
            var start = TimeSpan.ParseExact(match.Groups[2].Value, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
            var end = TimeSpan.ParseExact(match.Groups[3].Value, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
            var text = match.Groups[4].Value.Replace("\n", " ").Trim();
            cues.Add((start, end, text));
        }

        Console.WriteLine($"[WAV Generation] {cues.Count} cues found in {Path.GetFileName(vttFilePath)}.");

        // 2. Synthesize each caption and align
        var config = SpeechConfig.FromSubscription(azureSpeechKey, azureSpeechRegion);
        config.SpeechSynthesisLanguage = languageCode;
        config.SpeechSynthesisVoiceName = voiceName;
        var sampleRate = 16000;
        var allAudio = new List<byte[]>();
        var currentPosition = TimeSpan.Zero;

        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];

            // Insert silence if needed
            if (cue.start > currentPosition)
            {
                var silenceDuration = cue.start - currentPosition;
                var silenceBytes = new byte[(int)(silenceDuration.TotalSeconds * sampleRate * 2)]; // 16-bit mono
                allAudio.Add(silenceBytes);
                Console.WriteLine($"[WAV Generation] Inserted {silenceDuration.TotalMilliseconds:F0} ms silence before cue {i + 1}.");
            }

            Console.WriteLine($"[WAV Generation] Synthesizing cue {i + 1}/{cues.Count}: \"{cue.text}\"");
            byte[] result = await SpeechSynthesizerToWavBytesAsync(config, cue.text, sampleRate);
            allAudio.Add(result);

            currentPosition = cue.end;
        }

        // 3. Concatenate and save as WAV
        Console.WriteLine($"[WAV Generation] Writing output WAV: {Path.GetFileName(outputWavPath)}");
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, new WaveFormat(sampleRate, 16, 1)))
        {
            foreach (var audio in allAudio)
                writer.Write(audio, 0, audio.Length);
        }
        File.WriteAllBytes(outputWavPath, ms.ToArray());
        Console.WriteLine($"[WAV Generation] Done: {outputWavPath}");
    }

    // Helper: Synthesize speech to WAV bytes
    /// <summary>
    /// Synthesizes speech from the provided text using the specified Azure Speech configuration,
    /// and returns the resulting audio as a WAV byte array with the given sample rate.
    /// Throws an exception if speech synthesis fails.
    /// </summary>
    /// <param name="config">Azure SpeechConfig specifying subscription, region, language, and voice.</param>
    /// <param name="text">The text to synthesize into speech.</param>
    /// <param name="sampleRate">Desired sample rate for the output WAV audio (e.g., 16000 Hz).</param>
    /// <returns>A byte array containing the synthesized WAV audio data.</returns>
    private async Task<byte[]> SpeechSynthesizerToWavBytesAsync(SpeechConfig config, string text, int sampleRate)
    {
        using var audioStream = AudioOutputStream.CreatePullStream();
        using var audioConfig = AudioConfig.FromStreamOutput(audioStream);
        using var synthesizer = new SpeechSynthesizer(config, audioConfig);

        var result = await synthesizer.SpeakTextAsync(text);
        // Error thrown here
        if (result.Reason != ResultReason.SynthesizingAudioCompleted)
            throw new Exception($"Speech synthesis failed: {result.Reason}");

        return result.AudioData;
    }
    

    /// <summary>
    /// Initiates processing of a specified file by simulating a file creation event,
    /// allowing the file to be handled by the internal processing workflow.
    /// </summary>
    /// <param name="filePath">The full path to the file to process.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ProcessFileAsync(string filePath)
    {
        // Simulate a FileSystemEventArgs for reuse
        var e = new FileSystemEventArgs(WatcherChangeTypes.Created, Path.GetDirectoryName(filePath)!, Path.GetFileName(filePath));
        await ProcessFileInternalAsync(e);
    }


    /// <summary>
    /// Handles the complete processing workflow for a detected MP4 file event.
    /// Waits for the file to be ready, extracts audio, performs Vosk and Azure transcriptions,
    /// generates transcripts and VTT files, translates and synthesizes for target languages,
    /// organizes output files into language-specific folders, and logs all actions and errors.
    /// </summary>
    /// <param name="e">FileSystemEventArgs representing the file to process.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ProcessFileInternalAsync(FileSystemEventArgs e)
    {
        Console.WriteLine($"Processing file: {e.Name}");

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

                // Check if WAV file was created
                if (!File.Exists(outputFile))
                {
                    perFileLog.AppendLine($"[{DateTime.Now:O}] ERROR: Expected WAV file not found: {outputFile}");
                    return;
                }
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

                    // Check if Vosk transcript was created
                    if (!File.Exists(transcriptFileVosk))
                    {
                        perFileLog.AppendLine($"[{DateTime.Now:O}] ERROR: Expected Vosk transcript not found: {transcriptFileVosk}");
                    }
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
                    await TranscribeAndGenerateVttWithAzureAsync(e.FullPath, outputFile, transcriptFileAzure, vttFileAzure, targetLanguages, perFileLog);
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

        // Write and close the log file before moving files
        System.IO.File.WriteAllText(logFile, perFileLog.ToString());
        // At the end of OnFileCreated, before the finally block
        try
        {
            // Determine the base name and target directory
            string baseName = Path.GetFileNameWithoutExtension(e.Name);
            int version = 1;
            string targetDir = Path.Combine(_watchFolder, $"{baseName}");
            while (Directory.Exists(targetDir))
            {
                targetDir = Path.Combine(_watchFolder, $"{baseName}.v{++version}");
            }            
            // Create the directory if it doesn't exist
            Directory.CreateDirectory(targetDir);

            string targetDirLanguageSpecific = Path.Combine(targetDir, GetFolderForLanguage("en"));
            Directory.CreateDirectory(targetDirLanguageSpecific);
            foreach (var lang in targetLanguages)
            {
                targetDirLanguageSpecific = Path.Combine(targetDir, GetFolderForLanguage(lang));
                Directory.CreateDirectory(targetDirLanguageSpecific);
            }

            // List all files with the same base name (regardless of extension)
            var filesToMove = Directory.GetFiles(_watchFolder, baseName + ".*");

            foreach (var file in filesToMove)
            { 
                try
                {
                    // Skip if the file is already in the target directory
                    if (Path.GetDirectoryName(file) == targetDir)
                        continue;

                    string destinationFilename = Path.GetFileName(file).Replace(".azure", "");

                    string finalTargetDirectory = Path.Combine(targetDir, GetFolderForLanguage("en")); ;
                    foreach (var lang in targetLanguages)
                    {
                        if (destinationFilename.Contains("." + lang + "."))
                        { // Check if the file is for this language
                            finalTargetDirectory = Path.Combine(targetDir, GetFolderForLanguage(lang));
                            destinationFilename = destinationFilename.Replace("." + lang, "");
                            ; break;
                        }
                    }
                    string destFile = Path.Combine(finalTargetDirectory, Path.GetFileName(destinationFilename));
                    // If file exists in target, overwrite
                    try
                    {
                        File.Move(file, destFile, true);
                    }
                    catch (Exception ex1)
                    {
                        perFileLog.AppendLine($"[{DateTime.Now:O}] Error moving file - will try copying instead {file} to {targetDir}: {ex1}");
                        File.Copy(file, destFile, true);
                    }
                }
                catch (Exception ex)
                {
                    perFileLog.AppendLine($"[{DateTime.Now:O}] Error moving file {file} to {targetDir}: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            perFileLog.AppendLine($"[{DateTime.Now:O}] Error moving files to output folder: {ex}");
        }

    }
    
    /// <summary>
    /// Asynchronously combines a source MP4 video file and a WAV audio file into a new MP4 file using FFmpeg.
    /// The video stream is copied, and the audio is encoded as AAC. Throws an exception if the FFmpeg process fails.
    /// </summary>
    /// <param name="sourceVideoPath">Path to the source MP4 video file.</param>
    /// <param name="wavFilePath">Path to the WAV audio file to mux with the video.</param>
    /// <param name="outputMp4Path">Path to save the resulting MP4 file.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task MuxVideoWithWavAsync(string sourceVideoPath, string wavFilePath, string outputMp4Path)
    {
        Console.WriteLine($"[FFmpeg] Muxing video '{sourceVideoPath}' with audio '{wavFilePath}' to '{outputMp4Path}'");
        string arguments = $"-y -i \"{sourceVideoPath}\" -i \"{wavFilePath}\" -c:v copy -c:a aac -b:a 192k -map 0:v:0 -map 1:a:0 -shortest \"{outputMp4Path}\"";
        Console.WriteLine($"[FFmpeg] Command: {_ffmpegPath} {arguments}");

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (!process.Start())
            {
                Console.WriteLine("[FFmpeg] Failed to start FFmpeg process for muxing.");
                throw new Exception("FFmpeg process could not be started for muxing.");
            }

            // Read error asynchronously and wait for process exit
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string error = await errorTask;

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"[FFmpeg] FFmpeg exited with code {process.ExitCode} during muxing. Error output:\n{error}");
                throw new Exception($"FFmpeg failed with error: {error}");
            }

            Console.WriteLine($"[FFmpeg] Muxing succeeded: {outputMp4Path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FFmpeg] Exception during muxing: {ex.Message}");
            throw new Exception($"Muxing failed for {sourceVideoPath} + {wavFilePath}: {ex.Message}", ex);
        }
    }


}
