using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Oqtane.Models;

namespace Oqtane.Repository
{
    public class JobRepository : IJobRepository
    {
        private MasterDBContext _db;
        private readonly IMemoryCache _cache;

        public JobRepository(MasterDBContext context, IMemoryCache cache)
        {
            _db = context;
            _cache = cache;
        }

        public IEnumerable<Job> GetJobs()
        {
            return _cache.GetOrCreate("jobs", entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(30);
                return _db.Job.ToList();
            });
        }

        public Job AddJob(Job job)
        {
            _db.Job.Add(job);
            _db.SaveChanges();
            _cache.Remove("jobs");
            return job;
        }

        public Job UpdateJob(Job job)
        {
            _db.Entry(job).State = EntityState.Modified;
            _db.SaveChanges();
            _cache.Remove("jobs");
            return job;
        }

        public Job GetJob(int jobId)
        {
            return _db.Job.Find(jobId);
        }

        public Job GetJob(int jobId, bool tracking)
        {
            if (tracking)
            {
                return _db.Job.Find(jobId);
            }
            else
            {
                return _db.Job.AsNoTracking().FirstOrDefault(item => item.JobId == jobId);
            }

        }

        // Atomically claims the next run of a job. Returns true if this caller won the claim and
        // may run the job, false if someone else already holds it.
        //
        // The scheduler cannot decide this by reading Job.IsExecuting and then writing it back.
        // GetJobs() is served from IMemoryCache, and that cache is per process and is only
        // invalidated by writes made in the same process, so a second scheduler loop - in this
        // process, or on another instance sharing this database - can be holding a copy of the row
        // taken before the job last ran, and will conclude the job is still due. Evaluating the
        // guard and taking the claim in one UPDATE puts that decision in the database, which is the
        // only thing every scheduler loop shares. Concurrent statements serialize on the row, so
        // the loser re-evaluates its WHERE clause against the winner's committed IsExecuting and
        // updates nothing.
        public bool TryClaimJob(int jobId, DateTime utcNow)
        {
            var entityType = _db.Model.FindEntityType(typeof(Job));
            var storeObject = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table).Value;

            // Names come from the EF model rather than from literals because providers rewrite
            // them - see PostgreSQLDatabase.UpdateIdentityStoreTableNames(), which lower cases
            // every table and column in the model to match what its migrations created.
            var table = string.IsNullOrEmpty(storeObject.Schema)
                ? storeObject.Name
                : $"{storeObject.Schema}.{storeObject.Name}";
            var jobIdColumn = entityType.FindProperty(nameof(Job.JobId)).GetColumnName(storeObject);
            var isExecutingColumn = entityType.FindProperty(nameof(Job.IsExecuting)).GetColumnName(storeObject);
            var nextExecutionColumn = entityType.FindProperty(nameof(Job.NextExecution)).GetColumnName(storeObject);

            // Values are passed as parameters so that each provider binds its own types - notably
            // bit on SQL Server versus boolean on PostgreSQL.
            var sql = $"UPDATE {table} SET {isExecutingColumn} = {{0}} "
                    + $"WHERE {jobIdColumn} = {{1}} AND {isExecutingColumn} = {{2}} "
                    + $"AND ({nextExecutionColumn} IS NULL OR {nextExecutionColumn} <= {{3}})";

            var claimed = _db.Database.ExecuteSqlRaw(sql, true, jobId, false, utcNow) == 1;

            _cache.Remove("jobs");

            return claimed;
        }

        public void DeleteJob(int jobId)
        {
            Job job = _db.Job.Find(jobId);
            _db.Job.Remove(job);
            _db.SaveChanges();
            _cache.Remove("jobs");
        }
    }
}
