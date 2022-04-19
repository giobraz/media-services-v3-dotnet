using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Storage.Blobs;
using Common_Utils;
using LiveV2.Helpers;
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
    public class LiveEventAnalyzer
    {
        private readonly IAzureMediaServicesClient _client;
        private readonly IMediaServicesHelper _helper;
        private readonly ConfigWrapper _config;
        private readonly string _liveAssetName;
        private readonly string _uniqueness;

        private readonly Preset _preset;
        private readonly string _exPostAnalyzerTransformName;
        private readonly string _liveAnalyzerTransformName;

        public LiveEventAnalyzer(IAzureMediaServicesClient client, ConfigWrapper config, string uniqueness, string liveAssetName)
        {
            _client = client;
            _config = config;
            _uniqueness = uniqueness;
            _liveAssetName = liveAssetName;
            _helper = new MediaServicesHelper(_client, _config.ResourceGroup, _config.AccountName);

            // Create a video analyzer preset with video insights.
            _preset = new VideoAnalyzerPreset(
                    audioLanguage: "it-IT",
                    insightsToExtract: InsightsType.AudioInsightsOnly
                    );
            _exPostAnalyzerTransformName = "ExPostAnalyzerTransform-IT";
            _liveAnalyzerTransformName = "LiveCustomTransform-IT";
        }

        public async Task ExPostAnalysis()
        {
            Console.WriteLine("++++++++++++++++++++++++++++ Ex Post Analysis started ++++++++++++++++++++++++++++");

            // Variabili per l'analisi video
            string jobName = $"job-{_uniqueness}";
            string outputAssetName = $"output-{_uniqueness}";          

            // Ensure that you have the desired encoding Transform. This is really a one time setup operation.
            // Once it is created, we won't delete it.
            Transform transform = await _helper.GetOrCreateExPostAnalysisTransformAsync(_exPostAnalyzerTransformName, _preset);

            // Use the name of the created input asset to create the job input.
            JobInput jobInput = new JobInputAsset(assetName: _liveAssetName);

            // Output from the encoding Job must be written to an Asset, so let's create one
            Asset outputAsset = await _helper.CreateOutputAssetAsync(outputAssetName);

            Job job = await _helper.SubmitJobAsync(transform.Name, jobName, jobInput, outputAsset.Name);

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
                   _config.StorageAccountName, _config.StorageAccountKey);
                var blobContainerName = _config.StorageContainerName;
                var eventHubsConnectionString = _config.EventHubConnectionString;
                var eventHubName = _config.EventHubName;
                var consumerGroup = _config.EventHubConsumerGroup;

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
                    job = await _client.Jobs.GetAsync(_config.ResourceGroup, _config.AccountName, _exPostAnalyzerTransformName, jobName);
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
                job = await _helper.WaitForJobToFinishAsync(_exPostAnalyzerTransformName, jobName);
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
                Console.WriteLine("++++++++++++++++++++++++++++ Ex Post Analysis finished ++++++++++++++++++++++++++++");
            }
        }


        public async Task LiveAnalysis(int clipIndex)
        {
            Console.WriteLine($"++++++++++++++++++++++++++++ Live Analysis started for clip {clipIndex} ++++++++++++++++++++++++++++");

            // Variabili per l'analisi video
            string jobName = $"job-{_uniqueness}-{clipIndex}";
            string outputAssetName = $"output-{_uniqueness}-{clipIndex}";

            // Ensure that you have the desired encoding Transform. This is really a one time setup operation.
            // Once it is created, we won't delete it.
            Transform transform = await _helper.CreateLiveAnalysisTransform(_liveAnalyzerTransformName);

            // Use the name of the created input asset to create the job input.
            // 1 second == 10000000 ticks
            // esempio: ogni clip dura 30 secondi
            var start = new TimeSpan((clipIndex*30) * 10000000);
            var end = start.Add(new TimeSpan(0, 0, 30));

            JobInput jobInput = new JobInputAsset(assetName: _liveAssetName, 
                start: new AbsoluteClipTime(start),
                end: new AbsoluteClipTime(end),
                label: $"LiveClip{clipIndex}"
                );

            // Output from the encoding Job must be written to an Asset, so let's create one
            Asset outputAsset = await _helper.CreateOutputAssetAsync(outputAssetName);

            Job job = await _helper.SubmitJobAsync(transform.Name, jobName, jobInput, outputAsset.Name);

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
                   _config.StorageAccountName, _config.StorageAccountKey);
                var blobContainerName = _config.StorageContainerName;
                var eventHubsConnectionString = _config.EventHubConnectionString;
                var eventHubName = _config.EventHubName;
                var consumerGroup = _config.EventHubConsumerGroup;

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
                    job = await _client.Jobs.GetAsync(_config.ResourceGroup, _config.AccountName, _liveAnalyzerTransformName, jobName);
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
                job = await _helper.WaitForJobToFinishAsync(_liveAnalyzerTransformName, jobName);
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
                Console.WriteLine($"++++++++++++++++++++++++++++ Live Analysis ended for for clip {clipIndex} ++++++++++++++++++++++++++++");
            }
        }


        // GBR metodi spostati in MediaServicesHelper
        //
        //#region Private methods

        ///// <summary>
        ///// If the specified transform exists, get that transform.
        ///// If the it does not exist, creates a new transform with the specified output. 
        ///// In this case, the output is set to encode a video using one of the built-in encoding presets.
        ///// </summary>
        ///// <param name="client">The Media Services client.</param>
        ///// <param name="resourceGroupName">The name of the resource group within the Azure subscription.</param>
        ///// <param name="accountName"> The Media Services account name.</param>
        ///// <param name="transformName">The name of the transform.</param>
        ///// <returns></returns>
        //// <EnsureTransformExists>
        //private static async Task<Transform> GetOrCreateVideoAnalysisTransformAsync(IAzureMediaServicesClient client,
        //    string resourceGroupName,
        //    string accountName,
        //    string transformName,
        //    Preset preset)
        //{

        //    bool createTransform = false;
        //    Transform transform = null;
        //    try
        //    {
        //        // Does a transform already exist with the desired name? Assume that an existing Transform with the desired name
        //        // also uses the same recipe or Preset for processing content.
        //        transform = client.Transforms.Get(resourceGroupName, accountName, transformName);
        //    }
        //    catch (ErrorResponseException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        //    {
        //        createTransform = true;
        //    }

        //    if (createTransform)
        //    {
        //        // Start by defining the desired outputs.
        //        TransformOutput[] outputs = new TransformOutput[]
        //        {
        //            new TransformOutput(preset),
        //        };

        //        // Create the Transform with the output defined above
        //        transform = await client.Transforms.CreateOrUpdateAsync(resourceGroupName, accountName, transformName, outputs);
        //    }

        //    return transform;
        //}
        //// </EnsureTransformExists>


        ///// <summary>
        ///// Creates an ouput asset. The output from the encoding Job must be written to an Asset.
        ///// </summary>
        ///// <param name="client">The Media Services client.</param>
        ///// <param name="resourceGroupName">The name of the resource group within the Azure subscription.</param>
        ///// <param name="accountName"> The Media Services account name.</param>
        ///// <param name="assetName">The output asset name.</param>
        ///// <returns></returns>
        //// <CreateOutputAsset>
        //private static async Task<Asset> CreateOutputAssetAsync(IAzureMediaServicesClient client, string resourceGroupName, string accountName, string assetName)
        //{
        //    bool existingAsset = true;
        //    Asset outputAsset;
        //    try
        //    {
        //        // Check if an Asset already exists
        //        outputAsset = await client.Assets.GetAsync(resourceGroupName, accountName, assetName);
        //    }
        //    catch (ErrorResponseException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        //    {
        //        existingAsset = false;
        //    }

        //    Asset asset = new Asset();
        //    string outputAssetName = assetName;

        //    if (existingAsset)
        //    {
        //        // Name collision! In order to get the sample to work, let's just go ahead and create a unique asset name
        //        // Note that the returned Asset can have a different name than the one specified as an input parameter.
        //        // You may want to update this part to throw an Exception instead, and handle name collisions differently.
        //        string uniqueness = $"-{Guid.NewGuid():N}";
        //        outputAssetName += uniqueness;

        //        Console.WriteLine("Warning – found an existing Asset with name = " + assetName);
        //        Console.WriteLine("Creating an Asset with this name instead: " + outputAssetName);
        //    }

        //    return await client.Assets.CreateOrUpdateAsync(resourceGroupName, accountName, outputAssetName, asset);
        //}
        //// </CreateOutputAsset>


        ///// <summary>
        ///// Submits a request to Media Services to apply the specified Transform to a given input video.
        ///// </summary>
        ///// <param name="client">The Media Services client.</param>
        ///// <param name="resourceGroupName">The name of the resource group within the Azure subscription.</param>
        ///// <param name="accountName"> The Media Services account name.</param>
        ///// <param name="transformName">The name of the transform.</param>
        ///// <param name="jobName">The (unique) name of the job.</param>
        ///// <param name="jobInput"></param>
        ///// <param name="outputAssetName">The (unique) name of the  output asset that will store the result of the encoding job. </param>
        //// <SubmitJob>
        //private static async Task<Job> SubmitJobAsync(IAzureMediaServicesClient client,
        //    string resourceGroupName,
        //    string accountName,
        //    string transformName,
        //    string jobName,
        //    JobInput jobInput,
        //    string outputAssetName)
        //{
        //    JobOutput[] jobOutputs =
        //    {
        //        new JobOutputAsset(outputAssetName),
        //    };

        //    // In this example, we are assuming that the job name is unique.
        //    //
        //    // If you already have a job with the desired name, use the Jobs.Get method
        //    // to get the existing job. In Media Services v3, Get methods on entities returns null 
        //    // if the entity doesn't exist (a case-insensitive check on the name).
        //    Job job = await client.Jobs.CreateAsync(
        //        resourceGroupName,
        //        accountName,
        //        transformName,
        //        jobName,
        //        new Job
        //        {
        //            Input = jobInput,
        //            Outputs = jobOutputs,
        //        });

        //    return job;
        //}
        //// </SubmitJob>


        ///// <summary>
        ///// Polls Media Services for the status of the Job.
        ///// </summary>
        ///// <param name="client">The Media Services client.</param>
        ///// <param name="resourceGroupName">The name of the resource group within the Azure subscription.</param>
        ///// <param name="accountName"> The Media Services account name.</param>
        ///// <param name="transformName">The name of the transform.</param>
        ///// <param name="jobName">The name of the job you submitted.</param>
        ///// <returns></returns>
        //// <WaitForJobToFinish>
        //private static async Task<Job> WaitForJobToFinishAsync(IAzureMediaServicesClient client,
        //    string resourceGroupName,
        //    string accountName,
        //    string transformName,
        //    string jobName)
        //{
        //    const int SleepIntervalMs = 20 * 1000;

        //    Job job;
        //    do
        //    {
        //        job = await client.Jobs.GetAsync(resourceGroupName, accountName, transformName, jobName);

        //        Console.WriteLine($"Job is '{job.State}'.");
        //        for (int i = 0; i < job.Outputs.Count; i++)
        //        {
        //            JobOutput output = job.Outputs[i];
        //            Console.Write($"\tJobOutput[{i}] is '{output.State}'.");
        //            if (output.State == JobState.Processing)
        //            {
        //                Console.Write($"  Progress (%): '{output.Progress}'.");
        //            }

        //            Console.WriteLine();
        //        }

        //        if (job.State != JobState.Finished && job.State != JobState.Error && job.State != JobState.Canceled)
        //        {
        //            await Task.Delay(SleepIntervalMs);
        //        }
        //    }
        //    while (job.State != JobState.Finished && job.State != JobState.Error && job.State != JobState.Canceled);

        //    return job;
        //}
        //// </WaitForJobToFinish>

        //#endregion
    }
}
