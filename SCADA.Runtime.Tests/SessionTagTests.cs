using SCADA.Core.Alarms;
using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Runtime.Configuration;
using SCADA.Runtime.Polling;
using SCADA.Runtime.Runtime;
using SCADA.Runtime.TagTable;
using TagTableImpl = SCADA.Runtime.TagTable.TagTable;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Сессионные теги (docs/session-tags-concept.md): маршрутизация значений
/// между общей и локальной таблицами, курсор эпох, локальная запись без прав
/// и аудита, запреты конфигурации.
/// </summary>
public class SessionTagTests
{
    private static readonly TagId Shared = new(0);
    private static readonly TagId Session = new(1);
    private static readonly TagId SessionReadOnly = new(2);

    private static List<TagDefinition> Tags(double? sessionInit = null) =>
    [
        new TagDefinition
        {
            Id = Shared, Name = "Насос1.Обороты", DataType = TagDataType.Analog,
            DeviceId = new DeviceId(0), IsWritable = true
        },
        new TagDefinition
        {
            Id = Session, Name = "Экран.Режим", DataType = TagDataType.Analog,
            DeviceId = new DeviceId(1), IsWritable = true,
            Scope = TagScope.Session, InitValue = sessionInit
        },
        // непишущийся сессионный тег: так же будут выглядеть генерируемые
        // системные (@User.*, @Right.*) — префикс '@' в исходниках запрещён,
        // поэтому в тесте обычное имя
        new TagDefinition
        {
            Id = SessionReadOnly, Name = "Экран.ТолькоЧтение",
            DataType = TagDataType.Analog, DeviceId = new DeviceId(1),
            Scope = TagScope.Session
        }
    ];

    private static (SessionTagRouter Router, TagTableImpl SharedTable) Build(
        double? sessionInit = null)
    {
        var epochs = new EpochCounter();
        var shared = new TagTableImpl(4, epochs);
        var router = (SessionTagRouter)SessionTagRouter.Wrap(shared, Tags(sessionInit), epochs);
        return (router, shared);
    }

    // --- маршрутизация ---

    [Fact]
    public void Wrap_WithoutSessionTags_ReturnsSharedTableItself()
    {
        // проект без сессионных тегов не платит ни лишней проверкой,
        // ни вторым сканированием слотов
        var shared = new TagTableImpl(4);
        var tags = Tags().Where(t => t.Scope == TagScope.Shared).ToList();

        Assert.Same(shared, SessionTagRouter.Wrap(shared, tags, new EpochCounter()));
    }

    [Fact]
    public void SessionValue_LivesOutsideSharedTable()
    {
        var (router, shared) = Build();

        router.Write(Session, new TagValue(5, 100, Quality.Good));

        Assert.Equal(5, router.Read(Session).Value);
        // в общей таблице слот остался нетронутым: на сервере такого значения нет
        Assert.NotEqual(5, shared.Read(Session).Value);
        Assert.Equal(Quality.Bad, shared.Read(Session).Quality);
    }

    [Fact]
    public void SharedValue_GoesToSharedTable()
    {
        var (router, shared) = Build();

        router.Write(Shared, new TagValue(42, 100, Quality.Good));

        Assert.Equal(42, shared.Read(Shared).Value);
        Assert.Equal(42, router.Read(Shared).Value);
    }

    [Fact]
    public void SessionString_RoundTrips()
    {
        var (router, _) = Build();

        router.WriteString(SessionReadOnly, new StringTagValue("Иванов", 100, Quality.Good));

        Assert.Equal("Иванов", router.ReadString(SessionReadOnly).Text);
    }

    [Fact]
    public void InitValue_AppliedAtStart()
    {
        // пока персистентность отложена (§5), InitValue — способ задать
        // «вид по умолчанию» для всего объекта
        var (router, _) = Build(sessionInit: 1);

        Assert.Equal(1, router.Read(Session).Value);
        Assert.Equal(Quality.Good, router.Read(Session).Quality);
    }

    // --- эпохи ---

    [Fact]
    public void Epoch_StableWhenNothingChanged()
    {
        // канва пропускает пересчёт кадра, сравнивая эпоху с предыдущей —
        // эта оптимизация должна пережить появление второй таблицы
        var (router, _) = Build();
        router.Write(Shared, new TagValue(1, 0, Quality.Good));

        long first = router.CurrentEpoch;

        Assert.Equal(first, router.CurrentEpoch);
        Assert.Equal(first, router.CurrentEpoch);
    }

    [Fact]
    public void Epoch_SharedScale_SessionWriteAdvancesSameCounter()
    {
        // обе таблицы на одной шкале: номер записи в сессионную таблицу
        // продолжает общий ряд, а не начинает свой
        var (router, shared) = Build();
        router.Write(Shared, new TagValue(1, 0, Quality.Good));
        long afterShared = router.CurrentEpoch;

        router.Write(Session, new TagValue(2, 0, Quality.Good));

        Assert.Equal(afterShared + 1, router.CurrentEpoch);
        // общая таблица видит ту же шкалу, хотя записи в неё не было
        Assert.Equal(router.CurrentEpoch, shared.CurrentEpoch);
    }

    [Fact]
    public void Epoch_AdvancesOnBothTables_AndReportsChanges()
    {
        var (router, _) = Build();
        long start = router.CurrentEpoch;

        router.Write(Shared, new TagValue(1, 0, Quality.Good));
        long afterShared = router.CurrentEpoch;
        Assert.NotEqual(start, afterShared);

        router.Write(Session, new TagValue(2, 0, Quality.Good));
        long afterSession = router.CurrentEpoch;
        Assert.NotEqual(afterShared, afterSession);

        // изменения обеих таблиц приходят одним списком
        var buffer = new TagId[8];
        int count = router.GetChangedSince(start, buffer);
        Assert.Equal(2, count);
        Assert.Contains(Shared, buffer[..count].ToArray());
        Assert.Contains(Session, buffer[..count].ToArray());

        // с последнего курсора нового нет
        Assert.Equal(0, router.GetChangedSince(afterSession, buffer));
    }

    [Fact]
    public void Epoch_OnlySessionChanged_SharedNotReported()
    {
        var (router, _) = Build();
        router.Write(Shared, new TagValue(1, 0, Quality.Good));
        long afterShared = router.CurrentEpoch;

        router.Write(Session, new TagValue(2, 0, Quality.Good));

        var buffer = new TagId[8];
        int count = router.GetChangedSince(afterShared, buffer);

        Assert.Equal(1, count);
        Assert.Equal(Session, buffer[0]);
    }

    [Fact]
    public void Epoch_OldValue_StillResolvesExactly()
    {
        // общая шкала не забывает прошлое: сколько бы записей ни прошло,
        // старая эпоха остаётся точной точкой отсчёта, а не поводом
        // пересчитать весь кадр
        var (router, _) = Build();
        router.Write(Session, new TagValue(1, 0, Quality.Good));
        long beforeStream = router.CurrentEpoch;

        for (int i = 0; i < 500; i++)
            router.Write(Shared, new TagValue(i, 0, Quality.Good));

        var buffer = new TagId[8];
        int count = router.GetChangedSince(beforeStream, buffer);

        // изменился только общий тег — сессионный писали раньше отсечки
        Assert.Equal(1, count);
        Assert.Equal(Shared, buffer[0]);
    }

    [Fact]
    public void SessionTable_IsDenselyNumbered()
    {
        // сессионных тегов десятки: держать под них массив на весь диапазон
        // TagId значило бы просматривать лишние слоты на каждом кадре.
        // Наружу при этом идут обычные TagId, а не внутренние индексы
        var (router, _) = Build();

        router.Write(Session, new TagValue(9, 0, Quality.Good));
        long before = router.CurrentEpoch;
        router.Write(SessionReadOnly, new TagValue(1, 0, Quality.Good));

        var buffer = new TagId[8];
        int count = router.GetChangedSince(before, buffer);

        Assert.Equal(1, count);
        Assert.Equal(SessionReadOnly, buffer[0]);
        Assert.Equal(9, router.Read(Session).Value);
    }

    // --- операторская запись через клиента ---

    private static ProjectConfiguration Project() => new()
    {
        Name = "Session",
        Channels = [new ChannelDefinition { Id = new ChannelId(0), Name = "Ch", ChannelType = "none" }],
        Devices =
        [
            new DeviceDefinition { Id = new DeviceId(0), Name = "PLC", DriverName = "simulator", ChannelId = new ChannelId(0) },
            new DeviceDefinition { Id = new DeviceId(1), Name = "Local", DriverName = "internal", ChannelId = new ChannelId(0) }
        ],
        Tags = Tags()
    };

    private static (LocalRuntimeClient Client, SessionTagRouter Router) Client()
    {
        var epochs = new EpochCounter();
        var shared = new TagTableImpl(4, epochs);
        var config = Project();
        var router = (SessionTagRouter)SessionTagRouter.Wrap(shared, config.Tags, epochs);
        var engine = new PollingEngine(config, shared, TimeSpan.FromMilliseconds(50));
        var client = new LocalRuntimeClient(router, pollingEngine: engine, sessionTags: router);
        return (client, router);
    }

    [Fact]
    public async Task Write_SessionTag_IsLocalAndSucceeds()
    {
        var (client, router) = Client();

        var result = await client.WriteTagAsync(Session, 3, "оператор");

        Assert.Equal(TagWriteStatus.Ok, result.Status);
        Assert.Equal(3, router.Read(Session).Value);
    }

    [Fact]
    public async Task Write_SessionTagWithoutIsWritable_Rejected()
    {
        // системные сессионные теги (@User.*, @Right.*) не должны
        // переписываться действием со схемы
        var (client, _) = Client();

        var result = await client.WriteTagAsync(SessionReadOnly, 1, "оператор");

        Assert.Equal(TagWriteStatus.NotWritable, result.Status);
    }

    [Fact]
    public async Task Write_MixedBatch_KeepsResultOrder()
    {
        var (client, router) = Client();

        var results = await client.WriteTagsAsync(
        [
            new TagWriteItem(Session, 7),
            new TagWriteItem(Shared, 42),
            new TagWriteItem(SessionReadOnly, 1)
        ], "оператор");

        Assert.Equal(TagWriteStatus.Ok, results[0].Status);        // сессионный
        Assert.Equal(TagWriteStatus.NotWritable, results[2].Status); // сессионный только для чтения
        Assert.Equal(7, router.Read(Session).Value);
        // общий тег пошёл обычным путём — internal-устройства у него нет,
        // движок не запущен, но результат встал на своё место
        Assert.Equal(3, results.Count);
    }

    // --- запреты конфигурации ---

    private static IReadOnlyList<string> Validate(Action<TagDefinition> tweak,
        Action<ProjectConfiguration>? tweakProject = null)
    {
        var config = Project();
        tweak(config.Tags.Single(t => t.Id == Session));
        tweakProject?.Invoke(config);
        return ProjectValidator.Validate(config);
    }

    [Fact]
    public void Validation_ArchivedSessionTag_Fails()
    {
        var errors = Validate(t => t.IsArchived = true);

        Assert.Contains(errors, e => e.Contains("не архивируется"));
    }

    [Fact]
    public void Validation_LoggedSessionTag_Fails()
    {
        var errors = Validate(t => t.Logging = new TagLoggingConfiguration { LogOnChange = true });

        Assert.Contains(errors, e => e.Contains("не логируется"));
    }

    [Fact]
    public void Validation_PersistentSessionTag_FailsAsNotYetSupported()
    {
        var errors = Validate(t => t.IsPersistent = true);

        Assert.Contains(errors, e => e.Contains("ещё не поддерживается"));
    }

    [Fact]
    public void Validation_SessionTagOnRealDevice_Fails()
    {
        var errors = Validate(t => t.DeviceId = new DeviceId(0)); // simulator

        Assert.Contains(errors, e => e.Contains("только на внутреннем устройстве"));
    }

    [Fact]
    public void Validation_AlarmRuleOnSessionTag_Fails()
    {
        var errors = Validate(_ => { }, config => config.Alarms = new AlarmConfiguration
        {
            Rules =
            [
                new AlarmRule
                {
                    Name = "R1", Type = AlarmType.Threshold, TagName = "Экран.Режим",
                    Limits = [new ThresholdLimit { Kind = ThresholdKind.Hi, Value = 1 }]
                }
            ]
        });

        Assert.Contains(errors, e => e.Contains("ссылается на сессионный тег"));
    }

    [Fact]
    public void Validation_PlainSessionTag_Passes()
    {
        Assert.Empty(Validate(_ => { }));
    }
}
