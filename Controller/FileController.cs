using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenAI.Audio;
using Newtonsoft.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using FuzzySharp;
using MathNet.Numerics; // Fuzzy Matching Library

namespace FileUploadApi.Controllers
{   
    public class TranscriptionModel
    {
        public string Filename { get; set; } = string.Empty;
        public double Duration { get; set; }
        public List<WordModel> Words { get; set; } = new();
    }

    public class WordModel
    {
        public string Word { get; set; } = string.Empty;
        public double Start_time { get; set; }
        public double End_time { get; set; }
        public bool Edited { get; set; }
        public bool Qc { get; set; }
        public string Qc_word { get; set; } = string.Empty;
    }
    
    [ApiController]
    [Route("api/[controller]")]
    public class FileController : ControllerBase
    {
        private static readonly Dictionary<string, TranscriptionModel> Transcriptions = new();
        private const string UploadDir = "uploads";
        
        public FileController()
        {
            if (!Directory.Exists(UploadDir))
            {
                Directory.CreateDirectory(UploadDir);
            }
        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("No file uploaded.");
            }

            try
            {
                var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (string.IsNullOrEmpty(apiKey))
                {
                    return StatusCode(500, "OpenAI API key is missing.");
                }

                var filePath = Path.Combine(UploadDir, file.FileName);
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var audioClient = new AudioClient("whisper-1", apiKey);
                var transcriptionOptions = new AudioTranscriptionOptions
                {
                    ResponseFormat = AudioTranscriptionFormat.Verbose,
                    TimestampGranularities = AudioTimestampGranularities.Word | AudioTimestampGranularities.Segment,
                };

                AudioTranscription transcript = await audioClient.TranscribeAudioAsync(filePath, transcriptionOptions);

                var words = new List<WordModel>();
                foreach (var word in transcript.Words)
                {
                    var (qcWord, qcResult) = await RecheckWordWithWhisperAsync(
                        audioClient,
                        word.Word,
                        filePath,
                        word.StartTime.TotalMilliseconds,
                        word.EndTime.TotalMilliseconds
                    );
    
                    words.Add(new WordModel
                    {
                        Word = word.Word,
                        Start_time = Math.Round(word.StartTime.TotalMilliseconds, 3),
                        End_time = Math.Round(word.EndTime.TotalMilliseconds, 3),
                        Edited = false,
                        Qc = qcResult,
                        Qc_word = qcWord.Trim()
                    });
                }

                var result = new TranscriptionModel
                {
                    Filename = file.FileName,
                    Duration = transcript.Duration.HasValue ? transcript.Duration.Value.TotalSeconds : 0,
                    Words = words
                };

                Transcriptions[file.FileName] = result;
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"An error occurred: {ex.Message}");
            }
        }

    [HttpGet("get-json/{filename}")]
    public IActionResult GetTranscription(string filename)
    {
        if (Transcriptions.ContainsKey(filename))
        {
            return Ok(Transcriptions[filename]);
        }
        return NotFound(new { error = "File not found" });
    }

    [HttpPost("update-json/{filename}")]
    public IActionResult UpdateJson(string filename, [FromBody] TranscriptionModel updatedTranscription)
    {
        if (Transcriptions.ContainsKey(filename) && updatedTranscription != null)
        {
            Transcriptions[filename] = updatedTranscription;
            return Ok(new { message = "JSON updated successfully" });
        }
        return NotFound(new { error = "File not found" });
    }
        
// Modified Download method
        [HttpGet("download/{filename}")]
        public IActionResult DownloadJson(string filename)
        {
            if (!Transcriptions.TryGetValue(filename, out var transcription))
            {
                return NotFound(new { error = "File not found" });
            }

            // Use System.Text.Json instead of Newtonsoft.Json
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            return new JsonResult(transcription, options);
        }
        private async Task<(string transcribedWord, bool match)> RecheckWordWithWhisperAsync(
            AudioClient audioClient, 
            string originalWord,
            string audioFilePath,
            double startMs,
            double endMs)
        {
            try
            {
                var trimmedPath = TrimAudioWordSegment(audioFilePath, startMs, endMs);

                var options = new AudioTranscriptionOptions
                {
                    ResponseFormat = AudioTranscriptionFormat.Text,
                    Temperature = 0.1f
                };

                var result = await audioClient.TranscribeAudioAsync(trimmedPath, options);
                System.IO.File.Delete(trimmedPath);

                if (string.IsNullOrWhiteSpace(result.Value.Text))
                {
                    return ("Error: Whisper returned empty result", false);
                }

                var cleanResult = NormalizeWord(result.Value.Text);
                var cleanOriginal = NormalizeWord(originalWord);

                double similarity = FuzzyMatchingScore(cleanOriginal, cleanResult);

                return (cleanResult, similarity > 80); // Accept QC if similarity is above 80%
            }
            catch (Exception ex)
            {
                return ($"QC Error: {ex.Message}", false);
            }
        }

        private string NormalizeWord(string word)
        {
            if (string.IsNullOrEmpty(word)) return "";
            return new string(word.ToLowerInvariant()
                                .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                                .ToArray());
        }

        private double FuzzyMatchingScore(string word1, string word2)
        {
            return Fuzz.Ratio(word1, word2); // Fuzzy string matching using FuzzySharp
        }

        private string TrimAudioWordSegment(string inputPath, double startMs, double endMs)
        {
            var outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.wav");

            using (var reader = new AudioFileReader(inputPath))
            {
                var startTime = TimeSpan.FromMilliseconds(startMs);
                var endTime = TimeSpan.FromMilliseconds(endMs);

                using (var writer = new WaveFileWriter(outputPath, reader.WaveFormat))
                {
                    reader.CurrentTime = startTime;
                    var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];

                    while (reader.CurrentTime < endTime)
                    {
                        int samplesToRead = (int)((endTime - reader.CurrentTime).TotalSeconds * reader.WaveFormat.SampleRate);
                        int read = reader.Read(buffer, 0, Math.Min(samplesToRead, buffer.Length));

                        if (read == 0) break;
                        writer.WriteSamples(buffer, 0, read);
                    }
                }
            }

            return outputPath;
        }
    }
}
