using System.Text;

namespace ChartPacker.Core.Trash;

/// <summary>The mount points of a Linux system, read from <c>/proc/self/mountinfo</c>.</summary>
public sealed class MountTable
{
    private const string MountInfoPath = "/proc/self/mountinfo";

    private readonly string[] _mountPoints;

    private MountTable(IEnumerable<string> mountPoints) =>
        _mountPoints = [.. mountPoints.Distinct(StringComparer.Ordinal).OrderByDescending(m => m.Length)];

    /// <summary>The mount points listed in <paramref name="mountInfo"/>, text in the <c>/proc/self/mountinfo</c> format.</summary>
    public static MountTable Parse(string mountInfo)
    {
        ArgumentNullException.ThrowIfNull(mountInfo);

        var points = new List<string>();
        foreach (string line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "36 35 98:0 /mnt1 /mnt/parent rw,noatime master:1 - ext3 /dev/root rw": the fifth field.
            string[] fields = line.Split(' ');
            if (fields.Length > 4 && fields[4].StartsWith('/'))
                points.Add(Unescape(fields[4]));
        }

        return new MountTable(points);
    }

    /// <summary>This system's mount points, or <see langword="null"/> when they can't be read.</summary>
    public static MountTable? LoadCurrent()
    {
        try
        {
            return Parse(File.ReadAllText(MountInfoPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The mount point <paramref name="path"/> is on: the longest mount point it's at or under.</summary>
    public string? MountPointOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (string point in _mountPoints)
        {
            if (point == "/" || path == point || path.StartsWith(point + "/", StringComparison.Ordinal))
                return point;
        }

        return null;
    }

    /// <summary>Undoes mountinfo's octal escapes, such as <c>\040</c> for a space.</summary>
    private static string Unescape(string field)
    {
        if (!field.Contains('\\'))
            return field;

        var bytes = new List<byte>(field.Length);
        for (int i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length && IsOctal(field.AsSpan(i + 1, 3)))
            {
                bytes.Add((byte)Convert.ToInt32(field.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(field[i].ToString()));
            }
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static bool IsOctal(ReadOnlySpan<char> digits)
    {
        foreach (char c in digits)
        {
            if (c is < '0' or > '7')
                return false;
        }

        return true;
    }
}
