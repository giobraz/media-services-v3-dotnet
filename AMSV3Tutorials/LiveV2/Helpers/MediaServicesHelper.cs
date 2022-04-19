using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveV2.Helpers
{
    public class MediaServicesHelper : IMediaServicesHelper
    {
        private readonly IAzureMediaServicesClient _client;
        private readonly string _resourceGroupName;
        private readonly string _accountName;


        public MediaServicesHelper(IAzureMediaServicesClient client, string resourceGroupName, string accountName)
        {
            _client = client;
            _resourceGroupName = resourceGroupName;
            _accountName = accountName;

        }


        public async Task<Transform> GetOrCreateExPostAnalysisTransformAsync(string transformName, Preset preset)
        {

            bool createTransform = false;
            Transform transform = null;
            try
            {
                // Does a transform already exist with the desired name? Assume that an existing Transform with the desired name
                // also uses the same recipe or Preset for processing content.
                transform = _client.Transforms.Get(_resourceGroupName, _accountName, transformName);
            }
            catch (ErrorResponseException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                createTransform = true;
            }

            if (createTransform)
            {
                // Start by defining the desired outputs.
                TransformOutput[] outputs = new TransformOutput[]
                {
                    new TransformOutput(preset),
                };

                // Create the Transform with the output defined above
                transform = await _client.Transforms.CreateOrUpdateAsync(_resourceGroupName, _accountName, transformName, outputs);
            }

            return transform;
        }


        public async Task<Transform> CreateLiveAnalysisTransform(string transformName)
        {
            Console.WriteLine("Creating a custom transform...");

            var preset = new StandardEncoderPreset(
                            codecs: new Codec[]
                            {
                                // Add an AAC Audio layer for the audio encoding
                                new AacAudio(
                                    channels: 2,
                                    samplingRate: 48000,
                                    bitrate: 128000,
                                    profile: AacAudioProfile.AacLc
                                ),
                                // Next, add a H264Video for the video encoding
                               new H264Video (
                                    // Set the GOP interval to 2 seconds for both H264Layers
                                    keyFrameInterval:TimeSpan.FromSeconds(2),
                                     // Add H264Layers, one at HD and the other at SD. Assign a label that you can use for the output filename
                                    layers:  new H264Layer[]
                                    {
                                        new H264Layer (
                                            bitrate: 1000000, // Units are in bits per second
                                            width: "1280",
                                            height: "720",
                                            label: $"Clip" //"HD" // This label is used to modify the file name in the output formats
                                        )
                                        //,
                                        //new H264Layer (
                                        //    bitrate: 600000,
                                        //    width: "640",
                                        //    height: "360",
                                        //    label: "SD"
                                        //)
                                    }
                                ),
                                //// Also generate a set of PNG thumbnails
                                //new PngImage(
                                //    start: "25%",
                                //    step: "25%",
                                //    range: "80%",
                                //    layers: new PngLayer[]{
                                //        new PngLayer(
                                //            width: "50%",
                                //            height: "50%"
                                //        )
                                //    }
                                //)
                            },
                            // Specify the format for the output files - one for video+audio, and another for the thumbnails
                            formats: new Format[]
                            {
                                // Mux the H.264 video and AAC audio into MP4 files, using basename, label, bitrate and extension macros
                                // Note that since you have multiple H264Layers defined above, you have to use a macro that produces unique names per H264Layer
                                // Either {Label} or {Bitrate} should suffice
                                 
                                new Mp4Format(
                                    filenamePattern: "LiveOutput-{Label}"   //"LiveOutput-{Label}-{Bitrate}{Extension}"
                                ),
                                //new PngFormat(
                                //    filenamePattern:"Thumbnail-{Basename}-{Label}-{Index}{Extension}"
                                //)
                            }
                        );

            var preset1 = new VideoAnalyzerPreset(
                audioLanguage: "it-IT",
                insightsToExtract: InsightsType.AudioInsightsOnly
                );

            // Create a new Transform Outputs array - this defines the set of outputs for the Transform
            TransformOutput[] outputs = new TransformOutput[]
            {
                    // Create a new TransformOutput with a custom Standard Encoder Preset
                    // This demonstrates how to create custom codec and layer output settings

                  new TransformOutput(
                      preset: preset1,
                      onError: OnErrorType.StopProcessingJob,
                      relativePriority: Priority.Normal
                    )                     
            };

            string description = "A simple custom encoding transform with 2 MP4 bitrates";

            // Create the custom Transform with the outputs defined above
            Transform transform = await _client.Transforms.CreateOrUpdateAsync(_resourceGroupName, _accountName, transformName, outputs, description);

            return transform;
        }


        public async Task<Asset> CreateOutputAssetAsync(string assetName)
        {
            bool existingAsset = true;
            Asset outputAsset;
            try
            {
                // Check if an Asset already exists
                outputAsset = await _client.Assets.GetAsync(_resourceGroupName, _accountName, assetName);
            }
            catch (ErrorResponseException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                existingAsset = false;
            }

            Asset asset = new Asset();
            string outputAssetName = assetName;

            if (existingAsset)
            {
                // Name collision! In order to get the sample to work, let's just go ahead and create a unique asset name
                // Note that the returned Asset can have a different name than the one specified as an input parameter.
                // You may want to update this part to throw an Exception instead, and handle name collisions differently.
                string uniqueness = $"-{Guid.NewGuid():N}";
                outputAssetName += uniqueness;

                Console.WriteLine("Warning – found an existing Asset with name = " + assetName);
                Console.WriteLine("Creating an Asset with this name instead: " + outputAssetName);
            }

            return await _client.Assets.CreateOrUpdateAsync(_resourceGroupName, _accountName, outputAssetName, asset);
        }


        public async Task<Job> SubmitJobAsync(string transformName, string jobName, JobInput jobInput, string outputAssetName)
        {
            JobOutput[] jobOutputs =
            {
                new JobOutputAsset(outputAssetName),
            };

            // In this example, we are assuming that the job name is unique.
            //
            // If you already have a job with the desired name, use the Jobs.Get method
            // to get the existing job. In Media Services v3, Get methods on entities returns null 
            // if the entity doesn't exist (a case-insensitive check on the name).
            Job job = await _client.Jobs.CreateAsync(
                _resourceGroupName,
                _accountName,
                transformName,
                jobName,
                new Job
                {
                    Input = jobInput,
                    Outputs = jobOutputs,
                });

            return job;
        }


        public async Task<Job> WaitForJobToFinishAsync(string transformName, string jobName)
        {
            const int SleepIntervalMs = 20 * 1000;

            Job job;
            do
            {
                job = await _client.Jobs.GetAsync(_resourceGroupName, _accountName, transformName, jobName);

                Console.WriteLine($"Job is '{job.State}'.");
                for (int i = 0; i < job.Outputs.Count; i++)
                {
                    JobOutput output = job.Outputs[i];
                    Console.Write($"\tJobOutput[{i}] is '{output.State}'.");
                    if (output.State == JobState.Processing)
                    {
                        Console.Write($"  Progress (%): '{output.Progress}'.");
                    }

                    Console.WriteLine();
                }

                if (job.State != JobState.Finished && job.State != JobState.Error && job.State != JobState.Canceled)
                {
                    await Task.Delay(SleepIntervalMs);
                }
            }
            while (job.State != JobState.Finished && job.State != JobState.Error && job.State != JobState.Canceled);

            return job;
        }

    }
}
