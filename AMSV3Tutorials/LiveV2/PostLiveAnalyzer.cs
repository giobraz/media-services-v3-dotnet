using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Storage.Blobs;
using Common_Utils;
using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiveV2
{
    public static class PostLiveAnalyzer
    {
        public static async Task VideoAnalysis(IAzureMediaServicesClient client, string uniqueness, string liveAssetName, ConfigWrapper config)
        {
            Console.WriteLine("++++++++++++++++++++++++++++ Post Live Analysis started ++++++++++++++++++++++++++++");

            // Variabili per l'analisi video
            string jobName = $"job-{uniqueness}";
            string outputAssetName = $"output-{uniqueness}";
            var videoAnalyzerTransformName = "MyVideoAnalyzerTransformName-IT";

            // Create a video analyzer preset with video insights.
            Preset preset = new VideoAnalyzerPreset(
                    audioLanguage: "it-IT",
                    insightsToExtract: InsightsType.AudioInsightsOnly
                    );

            // Ensure that you have the desired encoding Transform. This is really a one time setup operation.
            // Once it is created, we won't delete it.
            Transform transform = await GetOrCreateVideoAnalysisTransformAsync(client, config.ResourceGroup, config.AccountName, videoAnalyzerTransformName, preset);

            // Use the name of the created input asset to create the job input.
            JobInput jobInput = new JobInputAsset(assetName: liveAssetName);

            // Output from the encoding Job must be written to an Asset, so let's create one
            Asset outputAsset = await CreateOutputAssetAsync(client, config.ResourceGroup, config.AccountName, outputAssetName);

            Job job = await SubmitJobAsync(client, config.ResourceGroup, config.AccountName, transform.Name, jobName, jobInput, outputAsset.Name);

            EventProcessorClient processorClient = null;
            BlobContainerClient storageClient = null;
            MediaServicesEventProcessor mediaEventProcessor = null;
            try
            {
                // First we will try to process Job events through Event Hub in real-time. If this fails for any reason,
                // we will fall-back on polling Job status instead.

                // Please refer README for Event Hub and storage settings.
                // A storage account is required to process the Event Hub events from the Event Grid subscription in this sample.

                // Create a new host to process events from an Event Hub.
                Console.WriteLine("Creating a new client to process events from an Event Hub...");
                var credential = new DefaultAzureCredential();
                var storageConnectionString = string.Format("DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1}",
                   config.StorageAccountName, config.StorageAccountKey);
                var blobContainerName = config.StorageContainerName;
                var eventHubsConnectionString = config.EventHubConnectionString;
                var eventHubName = config.EventHubName;
                var consumerGroup = config.EventHubConsumerGroup;

                storageClient = new BlobContainerClient(
                    storageConnectionString,
                    blobContainerName);

                processorClient = new EventProcessorClient(
                    storageClient,
                    consumerGroup,
                    eventHubsConnectionString,
                    eventHubName);

                // Create an AutoResetEvent to wait for the job to finish and pass it to EventProcessor so that it can be set when a final state event is received.
                AutoResetEvent jobWaitingEvent = new(false);

                // Create a Task list, adding a job waiting task and a timer task. Other tasks can be added too.
                IList<Task> tasks = new List<Task>();

                // Add a task to wait for the job to finish. The AutoResetEvent will be set when a final state is received by EventProcessor.
                Task jobTask = Task.Run(() =>
                jobWaitingEvent.WaitOne());
                tasks.Add(jobTask);

                // 30 minutes timeout.
                var cancellationSource = new CancellationTokenSource();
                var timeout = Task.Delay(30 * 60 * 1000, cancellationSource.Token);

                tasks.Add(timeout);
                mediaEventProcessor = new MediaServicesEventProcessor(jobName, jobWaitingEvent, null);
                processorClient.ProcessEventAsync += mediaEventProcessor.ProcessEventsAsync;
                processorClient.ProcessErrorAsync += mediaEventProcessor.ProcessErrorAsync;

                await processorClient.StartProcessingAsync(cancellationSource.Token);

                // Wait for tasks.
                if (await Task.WhenAny(tasks) == jobTask)
                {
                    // Job finished. Cancel the timer.
                    cancellationSource.Cancel();
                    // Get the latest status of the job.
                    job = await client.Jobs.GetAsync(config.ResourceGroup, config.AccountName, videoAnalyzerTransformName, jobName);
                }
                else
                {
                    // Timeout happened, Something might be wrong with job events. Fall-back on polling instead.
                    jobWaitingEvent.Set();
                    throw new Exception("Timeout happened.");
                }
            }
            catch (Exception)
            {
                Console.WriteLine("Warning: Failed to connect to Event Hub, please refer README for Event Hub and storage settings.");

                // Polling is not a recommended best practice for production applications because of the latency it introduces.
                // Overuse of this API may trigger throttling. Developers should instead use Event Grid.
                Console.WriteLine("Polling job status...");
                job = await WaitForJobToFinishAsync(client, config.ResourceGroup, config.AccountName, videoAnalyzerTransformName, jobName);
            }
            finally
            {
                if (processorClient != null)
                {
                    Console.WriteLine("Job final state received, Stopping the event processor...");
                    await processorClient.StopProcessingAsync();
                    Console.WriteLine();

                    // It is encouraged that you unregister your handlers when you have
                    // finished using the Event Processor to ensure proper cleanup.  This
                    // is especially important when using lambda expressions or handlers
                    // in any form that may contain closure scopes or hold other references.
                    processorClient.ProcessEventAsync -= mediaEventProcessor.ProcessEventsAsync;
                    processorClient.ProcessErrorAsync -= mediaEventProcessor.ProcessErrorAsync;
                }
            }

            if (job.State == JobState.Finished)
            {
                Console.WriteLine("++++++++++++++++++++++++++++ Post Live Analysis finished ++++++++++++++++++++++++++++");
            }
        }


        #region Private methods

        /// <summary>
        /// If the specified transform exists, get that transform.
        /// If the it does not exist, creates a new transform with the specified output. 
        /// In this case, the output is set to encode a video using one of the built-in encoding presets.
        /// </summary>
        /// <param name="client">The Media Services client.</param>
        /// <param name="resourceGroupName">The name of the resource group within the Azure subscription.</param>
        /// <param name="accountName"> The Media Services account name.</param>
        /// <param name="transformName">The name of the transform.</param>
        /// <returns></returns>
        // <EnsureTransformExists>
        private static async Task<Transform> GetOrCreateVideoAnalysisTransformAsync(IAzureMediaServicesClient client,
            string resourceGroupName,
            string accountName,
            string transformName,
            Preset preset)
        {

            bool createTransform = false;
            Transform transform = null;
            try
            {
                // Does a transform already exist with the desired name? Assume that an existing Transform with the desired name
                // also uses the same recipe or Preset for processing content.
                transform = client.Transforms.Get(resourceGroupName, accountName, transformName);
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
                transform = await client.Transforms.CreateOrUpdateAsync(resourceGroupName, accountName, transformName, outputs);
            }

            return transform;
        }
        // </EnsureTransformExists>


        /// <summary>
        /// Creates an ouput asset. The output from the encoding Job must be written to an Asset.
        /// </summary>
        /// <param name="client">The Media Services client.</param>
        /// <param name="resourceGroupName">The name of the resource group within the Azure subscription.</param>
        /// <param name="accountName"> The Media Services account name.</param>
        /// <param name="assetName">The output asset name.</param>
        /// <returns></returns>
        // <CreateOutputAsset>
        private static async Task<Asset> CreateOutputAssetAsync(IAzureMediaServicesClient client, string resourceGroupName, string accountName, string assetName)
        {
            bool existingAsset = true;
            Asset outputAsset;
            try
            {
                // Check if an Asset already exists
                outputAsset = await client.Assets.GetAsync(resourceGroupName, accountName, assetName);
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

            return await client.Assets.CreateOrUpdateAsync(resourceGroupName, accountName, outputAssetName, asset);
        }
        // </CreateOutputAsset>


        /// <summary>
        /// Submits a request to Media Services to apply the specified Transform to a given input video.
        /// </summary>
        /// <param name="client">The Media Services client.</param>
        /// <param name="resourceGroupName">The name of the resource group within the Azure subscription.</param>
        /// <param name="accountName"> The Media Services account name.</param>
        /// <param name="transformName">The name of the transform.</param>
        /// <param name="jobName">The (unique) name of the job.</param>
        /// <param name="jobInput"></param>
        /// <param name="outputAssetName">The (unique) name of the  output asset that will store the result of the encoding job. </param>
        // <SubmitJob>
        private static async Task<Job> SubmitJobAsync(IAzureMediaServicesClient client,
            string resourceGroupName,
            string accountName,
            string transformName,
            string jobName,
            JobInput jobInput,
            string outputAssetName)
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
            Job job = await client.Jobs.CreateAsync(
                resourceGroupName,
                accountName,
                transformName,
                jobName,
                new Job
                {
                    Input = jobInput,
                    Outputs = jobOutputs,
                });

            return job;
        }
        // </SubmitJob>


        /// <summary>
        /// Polls Media Services for the status of the Job.
        /// </summary>
        /// <param name="client">The Media Services client.</param>
        /// <param name="resourceGroupName">The name of the resource group within the Azure subscription.</param>
        /// <param name="accountName"> The Media Services account name.</param>
        /// <param name="transformName">The name of the transform.</param>
        /// <param name="jobName">The name of the job you submitted.</param>
        /// <returns></returns>
        // <WaitForJobToFinish>
        private static async Task<Job> WaitForJobToFinishAsync(IAzureMediaServicesClient client,
            string resourceGroupName,
            string accountName,
            string transformName,
            string jobName)
        {
            const int SleepIntervalMs = 20 * 1000;

            Job job;
            do
            {
                job = await client.Jobs.GetAsync(resourceGroupName, accountName, transformName, jobName);

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
        // </WaitForJobToFinish>

        #endregion
    }
}
