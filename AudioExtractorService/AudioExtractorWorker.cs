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

                // Translate WAV
                string translatedWavPath = Path.Combine(
                    Path.GetDirectoryName(transcriptFilePath)!,
                    Path.GetFileNameWithoutExtension(transcriptFilePath) + $".{lang}.wav"
                );
                await GenerateWavFromVttAsync(
                    translatedVttPath,
                    translatedWavPath,
                    _configuration["AzureSpeechKey"],
                    _configuration["AzureSpeechRegion"],
                    lang, // language code, e.g., "es-ES"
                    GetVoiceNameForLanguage(lang) // implement this helper to map language to a voice
                );
               
            }
        }
    }

    private string GetVoiceNameForLanguage(string lang)
    {
        // Map language codes to Azure voice names as needed
        return lang switch
        {
            "es" => "es-ES-AlvaroNeural",
            "fr" => "fr-FR-HenriNeural",
            "de" => "de-DE-ConradNeural",
            // Add more mappings as needed
            _ => "en-US-GuyNeural"
        };
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

    public async Task GenerateWavFromVttAsync(
        string vttFilePath,
        string outputWavPath,
        string azureSpeechKey,
        string azureSpeechRegion,
        string languageCode, // e.g., "es-ES"
        string voiceName     // e.g., "es-ES-AlvaroNeural"
    )
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
    private async Task<byte[]> SpeechSynthesizerToWavBytesAsync(SpeechConfig config, string text, int sampleRate)
    {
        using var audioStream = AudioOutputStream.CreatePullStream();
        using var audioConfig = AudioConfig.FromStreamOutput(audioStream);
        using var synthesizer = new SpeechSynthesizer(config, audioConfig);

        var result = await synthesizer.SpeakTextAsync(text);
        if (result.Reason != ResultReason.SynthesizingAudioCompleted)
            throw new Exception($"Speech synthesis failed: {result.Reason}");

        return result.AudioData;
    }

}
