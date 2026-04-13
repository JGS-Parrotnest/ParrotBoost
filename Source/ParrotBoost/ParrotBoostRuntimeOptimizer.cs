using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime;

namespace ParrotBoost;

internal sealed class ParrotBoostRuntimeOptimizer
{
    private readonly object _syncRoot = new();
    private readonly ConcurrentDictionary<string, string> _decompressionCache = new(StringComparer.Ordinal);
    private readonly Queue<byte[]> _compressedSnapshots = new();
    private readonly ProcessPriorityClass _baselinePriorityClass;
    private readonly int _baselineMinWorkerThreads;
    private readonly int _baselineMinCompletionThreads;
    private bool _boostProfileApplied;

    public ParrotBoostRuntimeOptimizer()
    {
        ThreadPool.GetMinThreads(out _baselineMinWorkerThreads, out _baselineMinCompletionThreads);
        _baselinePriorityClass = Process.GetCurrentProcess().PriorityClass;
    }

    public RuntimeOptimizationSnapshot UpdateRuntimeProfile(float cpuLoad, float gpuLoad, float? cpuTemp, float? gpuTemp)
    {
        bool boostEnabled = ParrotBoostSystemConfiguration.IsBoostEnabled();
        ApplyBoostProfile(boostEnabled);

        ProcessPriorityClass currentPriority = Process.GetCurrentProcess().PriorityClass;
        if (boostEnabled)
        {
            currentPriority = AdjustProcessPriority(cpuLoad);
        }

        var snapshot = new RuntimeOptimizationSnapshot(
            boostEnabled,
            DateTimeOffset.UtcNow,
            cpuLoad,
            gpuLoad,
            cpuTemp,
            gpuTemp,
            currentPriority,
            GetConfiguredWorkerThreads(),
            _decompressionCache.Count);

        if (boostEnabled)
        {
            CacheCompressedSnapshot(snapshot);
        }

        return snapshot;
    }

    public void ApplyBoostProfile(bool enabled)
    {
        lock (_syncRoot)
        {
            if (enabled)
            {
                TuneThreadPool();
                PrepareManagedMemory();
                _boostProfileApplied = true;
                return;
            }

            RestoreBaselineProfile();
        }
    }

    public string GetLatestMonitoringSummary()
    {
        lock (_syncRoot)
        {
            if (_compressedSnapshots.Count == 0)
            {
                return "No optimization samples captured.";
            }

            byte[] latest = _compressedSnapshots.Last();
            return DecompressWithCache(latest);
        }
    }

    private void CacheCompressedSnapshot(RuntimeOptimizationSnapshot snapshot)
    {
        string json = JsonSerializer.Serialize(snapshot);
        byte[] payload = Compress(json);

        lock (_syncRoot)
        {
            _compressedSnapshots.Enqueue(payload);
            while (_compressedSnapshots.Count > 24)
            {
                _compressedSnapshots.Dequeue();
            }

            while (_decompressionCache.Count > 32)
            {
                string? staleKey = _decompressionCache.Keys.FirstOrDefault();
                if (staleKey == null || !_decompressionCache.TryRemove(staleKey, out _))
                {
                    break;
                }
            }
        }
    }

    private void TuneThreadPool()
    {
        int targetWorkerThreads = Math.Max(_baselineMinWorkerThreads, Environment.ProcessorCount * 2);
        ThreadPool.SetMinThreads(targetWorkerThreads, _baselineMinCompletionThreads);
    }

    private static void PrepareManagedMemory()
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
    }

    private void RestoreBaselineProfile()
    {
        ThreadPool.SetMinThreads(_baselineMinWorkerThreads, _baselineMinCompletionThreads);
        GCSettings.LatencyMode = GCLatencyMode.Interactive;

        try
        {
            Process.GetCurrentProcess().PriorityClass = _baselinePriorityClass;
        }
        catch
        {
        }

        if (_boostProfileApplied)
        {
            _decompressionCache.Clear();
            _compressedSnapshots.Clear();
            _boostProfileApplied = false;
        }
    }

    private static ProcessPriorityClass AdjustProcessPriority(float cpuLoad)
    {
        ProcessPriorityClass targetPriority = cpuLoad switch
        {
            >= 85 => ProcessPriorityClass.BelowNormal,
            >= 60 => ProcessPriorityClass.Normal,
            _ => ProcessPriorityClass.AboveNormal
        };

        try
        {
            var process = Process.GetCurrentProcess();
            if (process.PriorityClass != targetPriority)
            {
                process.PriorityClass = targetPriority;
            }

            return process.PriorityClass;
        }
        catch
        {
            return Process.GetCurrentProcess().PriorityClass;
        }
    }

    private static int GetConfiguredWorkerThreads()
    {
        ThreadPool.GetMinThreads(out int workerThreads, out _);
        return workerThreads;
    }

    private string DecompressWithCache(byte[] payload)
    {
        string hash = Convert.ToHexString(SHA256.HashData(payload));
        return _decompressionCache.GetOrAdd(hash, _ => Decompress(payload));
    }

    private static byte[] Compress(string content)
    {
        byte[] input = Encoding.UTF8.GetBytes(content);
        using var output = new MemoryStream();

        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    private static string Decompress(byte[] payload)
    {
        using var input = new MemoryStream(payload);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);

        try
        {
            using var output = new MemoryStream();
            int bytesRead;
            while ((bytesRead = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, bytesRead);
            }

            return Encoding.UTF8.GetString(output.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal readonly record struct RuntimeOptimizationSnapshot(
        bool BoostEnabled,
        DateTimeOffset TimestampUtc,
        float CpuLoad,
        float GpuLoad,
        float? CpuTemperature,
        float? GpuTemperature,
        ProcessPriorityClass PriorityClass,
        int WorkerThreads,
        int CachedPayloads);
}
