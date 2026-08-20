using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Core.Schemes;
using SCADA.Core.Users;

namespace SCADA.Runtime.Configuration;

/// <summary>
/// Генератор системных сессионных тегов (docs/session-tags-concept.md §3) —
/// по образцу AlarmTagGenerator. Даёт схемам обычные привязки на то, что
/// персонально для АРМа: кто вошёл, какие у него права, есть ли связь.
///
/// Инварианты те же: детерминизм (один проект — одни Id), плотный ряд TagId,
/// имена с '@' (в исходниках запрещены валидатором), вызывается строго после
/// генераторов диагностики и аварий — новая подсистема не сдвигает уже
/// назначенные Id. Все теги — <see cref="TagScope.Session"/> и только
/// для чтения: переписать свой логин действием со схемы нельзя.
/// </summary>
public static class SessionTagGenerator
{
    /// <summary>Псевдоканал подсистемы сессий (ср. AlarmChannelId = -2).</summary>
    public static readonly ChannelId SessionChannelId = new(-3);

    public const string DeviceName = "@Session";

    public static void AppendSessionTags(ProjectConfiguration config)
    {
        var devices = config.Devices.ToList();
        var tags = config.Tags.ToList();
        int nextDeviceId = devices.Count == 0 ? 0 : devices.Max(d => d.Id.Value) + 1;
        int nextTagId = tags.Count == 0 ? 0 : tags.Max(t => t.Id.Value) + 1;

        var device = new DeviceDefinition
        {
            Id = new DeviceId(nextDeviceId),
            Name = DeviceName,
            Description = "Сессия оператора и состояние рабочего места",
            DriverName = "internal", // значения публикует клиент, а не опрос
            ChannelId = SessionChannelId
        };
        devices.Add(device);

        void AddTag(string name, TagDataType dataType) =>
            tags.Add(new TagDefinition
            {
                Id = new TagId(nextTagId++),
                Name = name,
                DataType = dataType,
                DeviceId = device.Id,
                Origin = TagOrigin.Session,
                Scope = TagScope.Session,
                // строковые теги не поддерживают InitValue (концепт §4.6)
                InitValue = dataType == TagDataType.String ? null : 0
            });

        AddTag(SessionSystemTags.UserName, TagDataType.String);
        AddTag(SessionSystemTags.UserIsAuthenticated, TagDataType.Analog);
        AddTag(SessionSystemTags.UserIsLocked, TagDataType.Analog);

        foreach (string permission in CollectPermissions(config))
            AddTag(SessionSystemTags.RightTag(permission), TagDataType.Analog);

        AddTag(SessionSystemTags.StationName, TagDataType.String);
        AddTag(SessionSystemTags.StationIsConnected, TagDataType.Analog);

        config.Devices = devices;
        config.Tags = tags;
    }

    /// <summary>
    /// Права, для которых заводятся теги: системные (их проверяет ядро,
    /// а UI по ним рисует доступность) плюс всё, что встречается в ролях
    /// проекта и в `RequiredRight` схем. Права схем включены намеренно:
    /// роль под них может появиться позже, а привязка `@Right.X` должна
    /// компилироваться уже сейчас. Порядок детерминирован сортировкой —
    /// от него зависят TagId.
    /// </summary>
    private static IEnumerable<string> CollectPermissions(ProjectConfiguration config)
    {
        var permissions = new SortedSet<string>(SystemPermissions.All, StringComparer.Ordinal);

        foreach (var role in config.Users.Roles)
            foreach (string permission in role.Permissions)
                if (!string.IsNullOrWhiteSpace(permission))
                    permissions.Add(permission);

        void FromElements(IReadOnlyList<SchemeElement> elements)
        {
            foreach (var element in elements)
            {
                if (!string.IsNullOrWhiteSpace(element.RequiredRight))
                    permissions.Add(element.RequiredRight);
                foreach (var schemeEvent in element.Events)
                    foreach (var action in schemeEvent.Actions)
                        if (!string.IsNullOrWhiteSpace(action.RequiredRight))
                            permissions.Add(action.RequiredRight);
            }
        }

        foreach (var scheme in config.Schemes)
        {
            if (!string.IsNullOrWhiteSpace(scheme.RequiredRight))
                permissions.Add(scheme.RequiredRight);
            FromElements(scheme.Elements);
        }
        foreach (var template in config.Templates)
            FromElements(template.Elements);

        return permissions;
    }
}
