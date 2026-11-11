namespace GitManager.Services.Repositories;

// 同一套系统的仓库（如 YingChuiNAS 与 yingchui-ios）在导航栏的同一远程分组内排在一起。
// 两个名称只要有一段相同的开头恰好在两边都落在词边界上（分隔符、大小写切换、字母数字切换或结尾），就视为同一套系统。
public static class RepositorySystemName
{
    private const int MinimumLength = 2;

    // 名称在每个词边界处截出的开头，去掉分隔符并转小写，例如 YingChuiNAS → ying、yingchui、yingchuinas。
    public static IReadOnlyList<string> Prefixes(string name)
    {
        var prefixes = new List<string>();
        var normalized = new System.Text.StringBuilder();
        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];
            if (!char.IsLetterOrDigit(current)) continue;
            normalized.Append(char.ToLowerInvariant(current));
            if (normalized.Length >= MinimumLength && IsBoundary(name, index + 1)) prefixes.Add(normalized.ToString());
        }
        return prefixes.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool IsBoundary(string name, int index)
    {
        if (index >= name.Length || !char.IsLetterOrDigit(name[index])) return true;
        char before = name[index - 1], current = name[index];
        if (char.IsDigit(before) != char.IsDigit(current)) return true;
        if (char.IsLower(before) && char.IsUpper(current)) return true;
        return char.IsUpper(before) && char.IsUpper(current) && index + 1 < name.Length && char.IsLower(name[index + 1]);
    }

    // 返回每一项在列表中的排序值：同一分组内属于同一套系统的仓库排在其中最靠前那一项的位置，其余保持原有顺序。
    public static int[] ListOrder(IReadOnlyList<(string Name, string Group)> items)
    {
        int count = items.Count;
        var parent = Enumerable.Range(0, count).ToArray();
        int Find(int index) => parent[index] == index ? index : parent[index] = Find(parent[index]);
        var owners = new Dictionary<(string Group, string Prefix), int>();
        for (int index = 0; index < count; index++)
        {
            foreach (string prefix in Prefixes(items[index].Name))
            {
                var key = (items[index].Group, prefix);
                if (!owners.TryGetValue(key, out int owner)) { owners[key] = index; continue; }
                int a = Find(owner), b = Find(index);
                if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); // 根始终是最靠前的一项
            }
        }
        return Enumerable.Range(0, count).Select(index => Find(index) * count + index).ToArray();
    }
}
