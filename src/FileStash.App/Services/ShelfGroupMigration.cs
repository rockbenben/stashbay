using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using FileStash.App.Resources;
using FileStash.Core.Abstractions;
using FileStash.Core.Localization;
using FileStash.Core.Models;

namespace FileStash.App.Services;

/// <summary>Only pre-kind records need name-based adoption; new user-named groups never do.</summary>
public static class ShelfGroupMigration
{
    public static async Task AdoptLegacyAsync(IStashGroupRepository repository, CancellationToken ct = default)
    {
        var all = await repository.GetAllAsync(ct);
        if (!all.Any(group => group.Kind == StashGroupKind.Legacy)) return;
        var manager = new ResourceManager("FileStash.App.Resources.Strings", typeof(Strings).Assembly);
        var defaults = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var culture = AppLanguageResolver.Resolve(language);
            if (manager.GetString(nameof(Strings.DefaultGroupName), culture) is string name) defaults.Add(name);
            if (manager.GetString(nameof(Strings.CompactTitle), culture) is string prefix) prefixes.Add(prefix);
        }
        prefixes.UnionWith(defaults); // Earlier shelves used the default label plus a timestamp.
        var hasDefault = all.Any(group => group.Kind == StashGroupKind.Default);
        foreach (var group in all.Where(group => group.Kind == StashGroupKind.Legacy).OrderBy(group => group.CreatedAt).ThenBy(group => group.Id))
        {
            var separator = group.Name.LastIndexOf(" · ", StringComparison.Ordinal);
            var temporary = separator > 0 && prefixes.Contains(group.Name[..separator])
                && Regex.IsMatch(group.Name[(separator + 3)..], @"^\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", RegexOptions.CultureInvariant);
            group.Kind = temporary ? StashGroupKind.Temporary : StashGroupKind.Saved;
            if (!hasDefault && defaults.Contains(group.Name))
            {
                group.Kind = StashGroupKind.Default;
                hasDefault = true;
            }
            await repository.UpdateAsync(group, ct);
        }
    }
}
