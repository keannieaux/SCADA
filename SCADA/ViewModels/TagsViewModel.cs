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
    private readonly TagId[] _changedBuffer;
    private long _epoch;

    public ObservableCollection<TagRowViewModel> Tags { get; } = new();

    public TagsViewModel(IRuntimeClient runtimeClient, ProjectConfiguration config)
    {
        _runtimeClient = runtimeClient;
        _rowsByTagId = new TagRowViewModel[config.Tags.Count];
        _changedBuffer = new TagId[config.Tags.Count];

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
        int count = _runtimeClient.GetChangedSince(_epoch, _changedBuffer);

        // GetChangedSince возвращает ПОЛНОЕ число изменившихся тегов, а буфер
        // заполняет лишь настолько, насколько хватило места (TagTable.cs);
        // вдобавок клиент сводит проектную и сессионную таблицы, поэтому
        // изменений бывает больше, чем тегов проекта, и приходят чужие Id.
        // Не влезло — перечитываем всё: иначе часть строк молча застынет
        // до следующего своего изменения.
        if (count > _changedBuffer.Length)
        {
            foreach (var row in Tags)
                Update(row);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                int index = _changedBuffer[i].Value;
                if (index >= 0 && index < _rowsByTagId.Length && _rowsByTagId[index] is { } row)
                    Update(row);
            }
        }

        _epoch = latestEpoch;
    }

    private void Update(TagRowViewModel row)
    {
        var value = _runtimeClient.Read(row.Id);
        row.Value = value.Value;
        row.Quality = value.Quality;
    }
}
