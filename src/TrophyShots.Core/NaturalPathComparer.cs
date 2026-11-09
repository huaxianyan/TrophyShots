namespace TrophyShots.Core;

/// <summary>
/// 按文件名自然排序：字母按序数比较（不分大小写），连续数字按数值比较，
/// 所以 <c>Replay 2</c> 排在 <c>Replay 10</c> 前面，而 <c>2026-10-22 00-01-37</c> 这种时间串也能直接按序排列。
/// </summary>
public sealed class NaturalPathComparer : IComparer<string>
{
    public static readonly NaturalPathComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0;
        int j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int startX = i;
                int startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                ReadOnlySpan<char> numberX = x.AsSpan(startX, i - startX).TrimStart('0');
                ReadOnlySpan<char> numberY = y.AsSpan(startY, j - startY).TrimStart('0');

                if (numberX.Length != numberY.Length)
                {
                    return numberX.Length.CompareTo(numberY.Length);
                }

                int numeric = numberX.SequenceCompareTo(numberY);
                if (numeric != 0)
                {
                    return numeric;
                }

                continue;
            }

            int left = char.ToUpperInvariant(x[i]);
            int right = char.ToUpperInvariant(y[j]);
            if (left != right)
            {
                return left.CompareTo(right);
            }

            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
