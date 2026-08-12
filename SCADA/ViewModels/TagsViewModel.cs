using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SCADA.Core.Tags;
using SCADA.Runtime.Runtime;

namespace SCADA.ViewModels;

public partial class TagRowViewModel : ObservableObject
{
    public TagId Id { get; }
    public string Name { get; }
    public string Units { get; }

    [ObservableProperty] public partial double Value { get; set; }
    [ObservableProperty] public partial Quality Quality { get; set; } = Quality.Bad;

    public TagRowViewModel(TagDefinition tag)
    {
        Id = tag.Id;
        Name = tag.Name;
        Units = tag.Units;
    }
}

public sealed class TagsViewModel : ViewModelBase
{
    private readonly IRuntimeClient _runtimeClient;
    private readonly TagRowViewModel[] _rowsByTagId;
    private long _epoch;

    public ObservableCollection<TagRowViewModel> Tags { get; } = new();

    public TagsViewModel(IRuntimeClient runtimeClient, ProjectConfiguration config)
    {
        _runtimeClient = runtimeClient;
        _rowsByTagId = new TagRowViewModel[config.Tags.Count];

        foreach (var tag in config.Tags)
        {
            var row = new TagRowViewModel(tag);
            _rowsByTagId[tag.Id.Value] = row;
            Tags.Add(row);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => Poll();
        timer.Start();
    }

    private void Poll()
    {
        long latestEpoch = _runtimeClient.CurrentEpoch;
        var changed = new TagId[_rowsByTagId.Length];
        int count = _runtimeClient.GetChangedSince(_epoch, changed);

        for (int i = 0; i < count; i++)
        {
            var value = _runtimeClient.Read(changed[i]);
            var row = _rowsByTagId[changed[i].Value];
            row.Value = value.Value;
            row.Quality = value.Quality;
        }

        _epoch = latestEpoch;
    }
}
