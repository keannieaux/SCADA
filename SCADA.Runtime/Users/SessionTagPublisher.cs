using SCADA.Core.Tags;
using SCADA.Core.Users;

namespace SCADA.Runtime.Users;

/// <summary>
/// Заполняет системные сессионные теги (docs/session-tags-concept.md §3)
/// по событиям сервиса сессий: вход, выход, блокировка. Не по тику — логин
/// и права меняются редко, а лишняя запись поднимала бы эпоху и заставляла
/// схему пересчитываться на пустом месте.
///
/// Пишет в клиентскую таблицу: значения персональны для АРМа, на сервере
/// таких слотов нет. Тегов может не быть вовсе (пакет собран старым
/// сборщиком) — тогда публикация молча пропускается, рантайм из-за
/// системных тегов падать не должен.
/// </summary>
public sealed class SessionTagPublisher : IDisposable
{
    private readonly ITagTable _tags;
    private readonly ISessionService _sessions;
    private readonly Func<string, TagId?> _resolve;

    /// <summary>Права, для которых заведены теги `@Right.*`. Набор знает
    /// конфигурация проекта, а не сервис сессий, поэтому приходит снаружи —
    /// и приходит целиком, чтобы снятые права гасились, а не залипали.</summary>
    private readonly IReadOnlyList<string> _rightPermissions;

    public SessionTagPublisher(ITagTable tags, ISessionService sessions,
        Func<string, TagId?> resolve, string stationName,
        IReadOnlyList<string> rightPermissions)
    {
        _tags = tags;
        _sessions = sessions;
        _resolve = resolve;
        _rightPermissions = rightPermissions;

        WriteString(SessionSystemTags.StationName, stationName);
        // мастер-АРМ сам себе сервер: связь есть всегда. У клиентского АРМа
        // значение начнёт менять транспорт (M7)
        WriteNumber(SessionSystemTags.StationIsConnected, 1);

        _sessions.SessionStarted += OnSessionStarted;
        _sessions.SessionEnded += OnSessionEnded;

        // в AuthMode.Local сессия существует уже на момент подписки
        if (_sessions.Current is { } current)
            Publish(current);
        else
            PublishNoSession();
    }

    public void Dispose()
    {
        _sessions.SessionStarted -= OnSessionStarted;
        _sessions.SessionEnded -= OnSessionEnded;
    }

    private void OnSessionStarted(SessionInfo session) => Publish(session);

    private void OnSessionEnded(SessionEndedEventArgs e)
    {
        // блокировка — не выход: пользователь тот же, но управление недоступно,
        // и права на схемах сжимаются до просмотра (users-plan.md §6.1)
        if (e.Action == SessionEndAction.Lock)
            Publish(e.Session);
        else
            PublishNoSession();
    }

    private void Publish(SessionInfo session)
    {
        WriteString(SessionSystemTags.UserName, session.Login);
        WriteNumber(SessionSystemTags.UserIsAuthenticated, 1);
        WriteNumber(SessionSystemTags.UserIsLocked, session.IsLocked ? 1 : 0);
        PublishRights(session);
    }

    private void PublishNoSession()
    {
        WriteString(SessionSystemTags.UserName, "");
        WriteNumber(SessionSystemTags.UserIsAuthenticated, 0);
        WriteNumber(SessionSystemTags.UserIsLocked, 0);
        PublishRights(session: null);
    }

    /// <summary>Права раскладываются по тегам `@Right.<имя>`: элемент схемы
    /// вяжется на них обычной привязкой, отдельная функция в ВМ не нужна.</summary>
    private void PublishRights(SessionInfo? session)
    {
        foreach (var tag in EnumerateRightTags())
        {
            bool granted = session is not null && session.HasPermission(tag.Permission);
            _tags.Write(tag.Id, new TagValue(granted ? 1 : 0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Quality.Good));
        }
    }

    private IEnumerable<(TagId Id, string Permission)> EnumerateRightTags()
    {
        foreach (string permission in _rightPermissions)
            if (_resolve(SessionSystemTags.RightTag(permission)) is { } id)
                yield return (id, permission);
    }

    private void WriteNumber(string name, double value)
    {
        if (_resolve(name) is { } id)
            _tags.Write(id, new TagValue(value,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Quality.Good));
    }

    private void WriteString(string name, string text)
    {
        if (_resolve(name) is { } id)
            _tags.WriteString(id, new StringTagValue(text,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Quality.Good));
    }
}
