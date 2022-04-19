using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveV2.Helpers
{
    public interface IMediaServicesHelper
    {
        Task<Transform> GetOrCreateExPostAnalysisTransformAsync(string transformName, Preset preset);

        Task<Transform> CreateLiveAnalysisTransform(string transformName);

        Task<Asset> CreateOutputAssetAsync(string assetName);

        Task<Job> SubmitJobAsync(string transformName, string jobName, JobInput jobInput, string outputAssetName);

        Task<Job> WaitForJobToFinishAsync(string transformName, string jobName);
    }
}
