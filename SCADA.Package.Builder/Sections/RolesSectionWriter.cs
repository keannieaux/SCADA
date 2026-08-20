using System.Text;
using SCADA.Core.Users;

namespace SCADA.Package.Builder.Sections;

/// <summary>
/// Сериализатор секции roles.bin (docs/users-plan.md §4.1). Зеркален
/// RolesSectionReader (SCADA.Package). Запись роли — [длина][поля...],
/// новые поля только в хвост записи или в хвост секции. До релиза
/// совместимость не поддерживается: поменял раскладку — пересобери пакет.
/// Пользователей в секции нет: они не входят в пакет (§3).
/// </summary>
public static class RolesSectionWriter
{
    public static byte[] Write(UsersConfiguration config)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        writer.Write(config.Roles.Count);
        foreach (var role in config.Roles)
        {
            // запись собирается в отдельный поток, чтобы узнать её длину
            using var recordStream = new MemoryStream();
            using (var record = new BinaryWriter(recordStream, Encoding.UTF8, leaveOpen: true))
            {
                record.Write(role.Name);
                record.Write(role.Permissions.Count);
                foreach (string permission in role.Permissions)
                    record.Write(permission);
                record.Flush();
            }

            writer.Write((int)recordStream.Length);
            writer.Write(recordStream.GetBuffer(), 0, (int)recordStream.Length);
        }

        writer.Write(config.MinPasswordLength);
        writer.Write(config.SessionTimeoutMinutes);

        writer.Flush();
        return stream.ToArray();
    }
}
