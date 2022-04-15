using Common_Utils;
using LiveV2.Models;
using Microsoft.Azure.Management.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace LiveV2
{
    public class SubclipLiveAnalyzer
    {
        private readonly static int intervalSec = 60;

        public static async Task VideoAnalysis(IAzureMediaServicesClient client, string uniqueness, string liveAssetName, ConfigWrapper config, string manifestUri)
        {
            // Variables
            int id = 0;
            string programid = "";
            string programName = "";
            string channelName = "";
            string programUrl = "";
            string programState = "";
            string lastState = "";

            TimeSpan startTime = TimeSpan.FromSeconds(0);
            TimeSpan duration = TimeSpan.FromSeconds(intervalSec);

            // Table storage to store and real the last timestamp processed
            // Retrieve the storage account from the connection string.
            CloudStorageAccount storageAccount = new CloudStorageAccount(new StorageCredentials(amsCredentials.StorageAccountName, amsCredentials.StorageAccountKey), true);

            // Create the table client.
            CloudTableClient tableClient = storageAccount.CreateCloudTableClient();

            // Retrieve a reference to the table.
            CloudTable table = tableClient.GetTableReference("liveanalytics");

            // Create the table if it doesn't exist.

            if (!table.CreateIfNotExists())
            {
                log.Info($"Table {table.Name} already exists");
            }
            else
            {
                log.Info($"Table {table.Name} created");
            }

            var lastendtimeInTable = ManifestHelpers.RetrieveLastEndTime(table, programid);

            // Get the manifest data (timestamps)
            var assetmanifestdata = GetManifestTimingData(manifestUri);
            //if (assetmanifestdata.Error)
            //{
            //    return req.CreateResponse(HttpStatusCode.InternalServerError, new { error = "Data cannot be read from program manifest." });
            //}

            Console.WriteLine("Timestamps: " + string.Join(",", assetmanifestdata.TimestampList.Select(n => n.ToString()).ToArray()));

            var livetime = TimeSpan.FromSeconds((double)assetmanifestdata.TimestampEndLastChunk / (double)assetmanifestdata.TimeScale);

            Console.WriteLine($"Livetime: {livetime}");

            startTime = ReturnTimeSpanOnGOP(assetmanifestdata, livetime.Subtract(TimeSpan.FromSeconds(intervalSec)));
            Console.WriteLine($"Value starttime : {startTime}");

            if (lastendtimeInTable != null)
            {
                lastState = lastendtimeInTable.ProgramState;
                Console.WriteLine($"Value ProgramState retrieved : {lastState}");

                var lastendtimeInTableValue = TimeSpan.Parse(lastendtimeInTable.LastEndTime);
                Console.WriteLine($"Value lastendtimeInTable retrieved : {lastendtimeInTableValue}");

                id = int.Parse(lastendtimeInTable.Id);
                Console.WriteLine($"Value id retrieved : {id}");

                if (lastendtimeInTableValue != null)
                {
                    var delta = (livetime - lastendtimeInTableValue - TimeSpan.FromSeconds(intervalSec)).Duration();
                    Console.WriteLine($"Delta: {delta}");

                    //if (delta < (new TimeSpan(0, 0, 3*intervalsec))) // less than 3 times the normal duration (3*60s)
                    if (delta < (TimeSpan.FromSeconds(3 * intervalSec))) // less than 3 times the normal duration (3*60s)
                    {
                        startTime = lastendtimeInTableValue;
                        Console.WriteLine($"Value new starttime : {startTime}");
                    }
                }
            }

            duration = livetime - startTime;
            Console.WriteLine($"Value duration: {duration}");
            if (duration == new TimeSpan(0)) // Duration is zero, this may happen sometimes !
            {               
                Console.WriteLine("ERROR! Stopping:uration of subclip is zero.");
                return;
            }

            // D:\home\site\wwwroot\Presets\LiveSubclip.json
            //string ConfigurationSubclip = File.ReadAllText(Path.Combine(System.IO.Directory.GetParent(execContext.FunctionDirectory).FullName, "presets", "LiveSubclip.json")).Replace("0:00:00.000000", starttime.Subtract(TimeSpan.FromMilliseconds(100)).ToString()).Replace("0:00:30.000000", duration.Add(TimeSpan.FromMilliseconds(200)).ToString());

            //int priority = 10;
            //if (data.priority != null)
            //{
            //    priority = (int)data.priority;
            //}


        }

        #region Private methods

        private static ManifestTimingData GetManifestTimingData(string manifestUri)
        // Parse the manifest and get data from it
        {
            ManifestTimingData response = new ManifestTimingData() { IsLive = false, Error = false, TimestampOffset = 0, TimestampList = new List<ulong>(), DiscontinuityDetected = false };

            try
            {
                    //Console.WriteLine($"Asset URI {manifestUri}");

                    XDocument manifest = XDocument.Load(manifestUri);

                    //log.Info($"manifest {manifest}");
                    var smoothmedia = manifest.Element("SmoothStreamingMedia");
                    var videotrack = smoothmedia.Elements("StreamIndex").Where(a => a.Attribute("Type").Value == "video");

                    // TIMESCALE
                    string timescalefrommanifest = smoothmedia.Attribute("TimeScale").Value;
                    if (videotrack.FirstOrDefault().Attribute("TimeScale") != null) // there is timescale value in the video track. Let's take this one.
                    {
                        timescalefrommanifest = videotrack.FirstOrDefault().Attribute("TimeScale").Value;
                    }
                    ulong timescale = ulong.Parse(timescalefrommanifest);
                    response.TimeScale = (ulong?)timescale;

                    // Timestamp offset
                    if (videotrack.FirstOrDefault().Element("c").Attribute("t") != null)
                    {
                        response.TimestampOffset = ulong.Parse(videotrack.FirstOrDefault().Element("c").Attribute("t").Value);
                    }
                    else
                    {
                        response.TimestampOffset = 0; // no timestamp, so it should be 0
                    }

                    ulong totalduration = 0;
                    ulong durationpreviouschunk = 0;
                    ulong durationchunk;
                    int repeatchunk;
                    foreach (var chunk in videotrack.Elements("c"))
                    {
                        durationchunk = chunk.Attribute("d") != null ? ulong.Parse(chunk.Attribute("d").Value) : 0;
                        Console.WriteLine($"duration d {durationchunk}");

                        repeatchunk = chunk.Attribute("r") != null ? int.Parse(chunk.Attribute("r").Value) : 1;
                        Console.WriteLine($"repeat r {repeatchunk}");

                        if (chunk.Attribute("t") != null)
                        {
                            ulong tvalue = ulong.Parse(chunk.Attribute("t").Value);
                            response.TimestampList.Add(tvalue);
                            if (tvalue != response.TimestampOffset)
                            {
                                totalduration = tvalue - response.TimestampOffset; // Discountinuity ? We calculate the duration from the offset
                                response.DiscontinuityDetected = true; // let's flag it
                            }
                        }
                        else
                        {
                            response.TimestampList.Add(response.TimestampList[response.TimestampList.Count() - 1] + durationpreviouschunk);
                        }

                        totalduration += durationchunk * (ulong)repeatchunk;

                        for (int i = 1; i < repeatchunk; i++)
                        {
                            response.TimestampList.Add(response.TimestampList[response.TimestampList.Count() - 1] + durationchunk);
                        }

                        durationpreviouschunk = durationchunk;

                    }
                    response.TimestampEndLastChunk = response.TimestampList[response.TimestampList.Count() - 1] + durationpreviouschunk;

                    if (smoothmedia.Attribute("IsLive") != null && smoothmedia.Attribute("IsLive").Value == "TRUE")
                    { // Live asset.... No duration to read (but we can read scaling and compute duration if no gap)
                        response.IsLive = true;
                        response.AssetDuration = TimeSpan.FromSeconds((double)totalduration / ((double)timescale));
                    }
                    else
                    {
                        totalduration = ulong.Parse(smoothmedia.Attribute("Duration").Value);
                        response.AssetDuration = TimeSpan.FromSeconds((double)totalduration / ((double)timescale));
                    }
            }
            catch (Exception ex)
            {
                response.Error = true;
            }
            return response;
        }


        // return the exact timespan on GOP
        private static TimeSpan ReturnTimeSpanOnGOP(ManifestTimingData data, TimeSpan ts)
        {
            var response = ts;
            ulong timestamp = (ulong)(ts.TotalSeconds * data.TimeScale);

            int i = 0;
            foreach (var t in data.TimestampList)
            {
                if (t < timestamp && i < (data.TimestampList.Count - 1) && timestamp < data.TimestampList[i + 1])
                {
                    response = TimeSpan.FromSeconds((double)t / (double)data.TimeScale);
                    break;
                }
                i++;
            }
            return response;
        }

        #endregion
    }
}
