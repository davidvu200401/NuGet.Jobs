﻿using NuGet.Jobs.Common;

namespace Stats.Replicator
{
    class Program
    {
        static void Main(string[] args)
        {
            var job = new Job();
            JobRunner.Run(job, args).Wait();
        }
    }
}
