namespace Shipline.Core.Pipelines;

/// <summary>Finds the word the user probably meant, for "did you mean ...?" hints.</summary>
internal static class Spelling
{
    private const int MaxEdits = 2;

    public static string? Suggest(string word, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string candidate in candidates)
        {
            int distance = EditDistance(word, candidate);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        bool closeEnough = bestDistance <= MaxEdits && bestDistance < word.Length;
        if (!closeEnough)
        {
            return null;
        }

        return best;
    }

    /// <summary>The Levenshtein distance: how many single-character inserts, deletes or changes turn one word into the other.</summary>
    public static int EditDistance(string first, string second)
    {
        int[] previousRow = new int[second.Length + 1];
        int[] currentRow = new int[second.Length + 1];
        for (int column = 0; column <= second.Length; column++)
        {
            previousRow[column] = column;
        }

        for (int row = 1; row <= first.Length; row++)
        {
            currentRow[0] = row;
            for (int column = 1; column <= second.Length; column++)
            {
                int changeCost = first[row - 1] == second[column - 1] ? 0 : 1;
                int delete = previousRow[column] + 1;
                int insert = currentRow[column - 1] + 1;
                int change = previousRow[column - 1] + changeCost;
                currentRow[column] = Math.Min(Math.Min(delete, insert), change);
            }

            int[] swap = previousRow;
            previousRow = currentRow;
            currentRow = swap;
        }

        return previousRow[second.Length];
    }
}
