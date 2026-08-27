using System;
using System.Collections.Generic;
using Oqtane.Models;

namespace Oqtane.Repository
{
    public interface IJobRepository
    {
        IEnumerable<Job> GetJobs();
        Job AddJob(Job job);
        Job UpdateJob(Job job);
        Job GetJob(int jobId);
        Job GetJob(int jobId, bool tracking);
        bool TryClaimJob(int jobId, DateTime utcNow);
        void DeleteJob(int jobId);
    }
}
