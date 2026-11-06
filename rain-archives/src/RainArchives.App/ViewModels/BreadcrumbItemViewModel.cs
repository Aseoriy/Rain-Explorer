using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public sealed record BreadcrumbItemViewModel(
    string Name,
    EntryId? EntryId,
    string AutomationName = "Archive location",
    bool IsCurrent = false);
