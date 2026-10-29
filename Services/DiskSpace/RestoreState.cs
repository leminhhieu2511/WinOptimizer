using System.Text.Json;

namespace WinOptimizer.Services.DiskSpace;

/// <summary>Steps of a disk space move. Recorded in the journal when a step starts (Done = step 7 finished).</summary>
public enum MoveStep { Checks = 0, ShrinkSource = 1, LockSource = 2, MoveSource = 3, CommitSource = 4, MoveRecovery = 5, ExtendTarget = 6, Verify = 7, Done = 8 }

/// <summary>
/// Everything a resume needs to finish (or a cancel needs to undo) a move, stored as JSON in the journal's app
/// blob. Attributes are the EXACT GPT bits read through DiskMove.dll before anything was changed (never the
/// approximate <see cref="PartitionSnapshot.GptAttributes"/>). Starts and sizes are the values before the move.
/// </summary>
/// <param name="SourceLetter">Original drive letter of the source, '\0' if it had none.</param>
/// <param name="Amount">X: bytes taken from the source and given to the target; also the shift of every move.</param>
public sealed record RestoreState(Guid DiskGuid, Guid SourceGuid, char SourceLetter, ulong SourceGptAttributes,
    Guid? RecoveryGuid, bool WinReWasEnabled, Guid TargetGuid, long Amount, int Version = 1)
{
    public const int CurrentVersion = 1;

    /// <summary>"\\?\Volume{...}\" of the source (unchanged by the move; used to lock it and to re-add its letter).</summary>
    public string? SourceVolumePath { get; init; }
    /// <summary>Start of the source (it does not change when shrinking).</summary>
    public long SourceStart { get; init; }
    /// <summary>Size of the source before step 1.</summary>
    public long SourceOriginalSize { get; init; }
    public long TargetOriginalSize { get; init; }
    public long RecoveryStart { get; init; }
    public long RecoverySize { get; init; }
    /// <summary>Exact original GPT attributes of the Recovery partition.</summary>
    public ulong RecoveryGptAttributes { get; init; }
    public string? RecoveryVolumePath { get; init; }
    public int SectorSize { get; init; }
    /// <summary>Whether the run that created the journal manages WinRE (reagentc); a resume follows this value.</summary>
    public bool ManageWinRe { get; init; }

    public long SourceShrunkSize => SourceOriginalSize - Amount;
    public long SourceNewStart => SourceStart + Amount;
    public long RecoveryNewStart => RecoveryStart + Amount;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public byte[] ToJsonBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>Parses the app blob; throws <see cref="DiskMoveException"/> if it is not a RestoreState of this version.</summary>
    public static RestoreState FromJsonBytes(ReadOnlySpan<byte> json)
    {
        RestoreState? s;
        try
        {
            s = JsonSerializer.Deserialize<RestoreState>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new DiskMoveException("The journal's restore information is not readable.", ex);
        }
        if (s is null || s.Version != CurrentVersion)
            throw new DiskMoveException($"The journal's restore information has an unsupported version ({s?.Version}).");
        return s;
    }
}

/// <summary>Progress of a run: the current step and, for the copy steps, bytes copied.</summary>
public sealed record RunProgress(MoveStep Step, long BytesDone, long BytesTotal, string Message);

/// <summary>One partition move as handed to DiskMove.dll (dm_move_plan). Bytes; the partition moves right by Shift.</summary>
public sealed record NativeMovePlan(Guid DiskGuid, Guid PartitionGuid, int SectorSize, long OldStart, long Length,
    long Shift, IReadOnlyList<(long Offset, long Length)> Extents);

/// <summary>The journal's newest progress slot (dm_journal_info).</summary>
public sealed record JournalInfo(int MoveCount, MoveStep Step, int MoveIndex, long ChunksDone, long[] ChunkCount)
{
    /// <summary>True when move <paramref name="index"/> has copied every chunk.</summary>
    public bool MoveComplete(int index) =>
        MoveIndex > index || (MoveIndex == index && ChunksDone == ChunkCount[index]);
}

/// <summary>Base of the runner's errors.</summary>
public class DiskMoveException : Exception
{
    public DiskMoveException(string message) : base(message) { }
    public DiskMoveException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The recheck right before step 2 found a Blocker or FixFirst; step 1 was undone.</summary>
public sealed class DiskMoveAbortedException(IReadOnlyList<CheckResult> results)
    : DiskMoveException("The move was stopped before any data was moved: " +
                        string.Join(" ", results.Where(r => r.Severity != CheckSeverity.Warning).Select(r => r.Message)))
{
    public IReadOnlyList<CheckResult> Results { get; } = results;
}

/// <summary>The disk does not look like any state the journal allows; nothing was changed. Never guess.</summary>
public sealed class DiskMoveStateMismatchException(string message) : DiskMoveException(message);

/// <summary>A DiskMove.dll call failed with a dm_status.</summary>
public sealed class DiskMoveNativeException(int status, string operation)
    : DiskMoveException($"{operation} failed: {StatusName(status)} ({status})")
{
    public int Status { get; } = status;

    public static string StatusName(int s) => s switch
    {
        0 => "DM_OK", 1 => "DM_E_INVALID_ARG", 2 => "DM_E_INVALID_PLAN", 3 => "DM_E_IO", 4 => "DM_E_VERIFY",
        5 => "DM_E_JOURNAL_CORRUPT", 6 => "DM_E_JOURNAL_MISMATCH", 7 => "DM_E_STOPPED", 8 => "DM_E_NOMEM",
        9 => "DM_E_NOT_FOUND", 10 => "DM_E_ACCESS", 11 => "DM_E_LOCKED", _ => "DM_E_?",
    };
}

/// <summary>Step 7 found the result not as planned; the journal is kept.</summary>
public sealed class DiskMoveVerificationException(string message) : DiskMoveException(message);
