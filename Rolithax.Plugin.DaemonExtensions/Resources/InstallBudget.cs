namespace Rolithax.Plugin.DaemonExtensions.Resources;

/// <summary>
/// 单个安装任务的暂存空间配额：下载的压缩包、整合包里的下载文件和解压出的覆盖文件共用同一个总量上限，
/// 同时按文件数量和磁盘剩余空间兜底，避免恶意或损坏的整合包耗尽宿主磁盘。
/// 声明大小（清单 fileSize、压缩条目 Length）只用于提前拒绝，实际写入的字节数才是计量依据。
/// </summary>
internal sealed class InstallBudget
{
    public const long DefaultTotalLimit = 4L * 1024 * 1024 * 1024;
    public const long DefaultMinFreeBytes = 1024L * 1024 * 1024;
    public const long SingleFileLimit = 1024L * 1024 * 1024;
    public const int MaxFiles = 20000;
    private const long DiskCheckStep = 64L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly long _totalLimit;
    private readonly long _minFreeBytes;
    private readonly Func<long?>? _freeSpace;
    private long _used;
    private long _nextDiskCheck = DiskCheckStep;
    private int _files;

    /// <param name="totalLimit">暂存区字节总量上限。</param>
    /// <param name="freeSpace">返回暂存区所在磁盘的可用字节数；返回 null 表示无法获取，跳过磁盘检查。</param>
    /// <param name="minFreeBytes">安装期间必须给宿主保留的最小剩余空间。</param>
    public InstallBudget(long totalLimit = DefaultTotalLimit, Func<long?>? freeSpace = null, long minFreeBytes = DefaultMinFreeBytes)
    {
        _totalLimit = totalLimit;
        _freeSpace = freeSpace;
        _minFreeBytes = minFreeBytes;
    }

    public long Used
    {
        get { lock (_gate) return _used; }
    }

    /// <summary>即将写入 <paramref name="bytes"/> 字节前的预检查（按声明大小提前拒绝，不计量）。</summary>
    public void EnsureFits(long bytes)
    {
        if (bytes < 0) throw new InvalidDataException("资源大小无效");
        lock (_gate)
        {
            if (bytes > SingleFileLimit) throw new InvalidDataException("单个资源大小超过 1 GB");
            if (_used + bytes > _totalLimit) throw TotalExceeded();
            var free = _freeSpace?.Invoke();
            if (free.HasValue && free.Value - bytes < _minFreeBytes) throw DiskLow();
        }
    }

    /// <summary>记录实际写入暂存区的字节数；超过总量上限或磁盘余量不足时抛出。</summary>
    public void Charge(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            _used += bytes;
            if (_used > _totalLimit) throw TotalExceeded();
            if (_used >= _nextDiskCheck)
            {
                _nextDiskCheck = _used + DiskCheckStep;
                var free = _freeSpace?.Invoke();
                if (free.HasValue && free.Value < _minFreeBytes) throw DiskLow();
            }
        }
    }

    /// <summary>暂存文件被删除后归还对应的配额（例如展开完成的 .mrpack 本体）。</summary>
    public void Release(long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate) _used = Math.Max(0, _used - bytes);
    }

    /// <summary>登记一个将要落到暂存区的文件。</summary>
    public void AddFile()
    {
        lock (_gate)
        {
            if (++_files > MaxFiles) throw new InvalidDataException($"资源文件数量超过 {MaxFiles} 个上限");
        }
    }

    private InvalidDataException TotalExceeded() =>
        new($"资源安装总量超过 {_totalLimit / 1024.0 / 1024 / 1024:0.##} GB 上限");

    private static InvalidDataException DiskLow() => new("磁盘剩余空间不足，已停止安装");
}
