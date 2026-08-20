using System.Text;
using SCADA.Core.Users;

namespace SCADA.Package.Sections;

/// <summary>
/// Читатель секции roles.bin. Зеркален RolesSectionWriter
/// (SCADA.Package.Builder). Длина записи перед полями позволяет пропускать
/// неизвестные хвосты записей (поля добавляются только в конец).
/// </summary>
public static class RolesSectionReader
{
    public static UsersConfiguration Read(byte[] section)
    {
        using var stream = new MemoryStream(section);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        int roleCount = reader.ReadInt32();
        var roles = new List<RoleDefinition>(roleCount);
        for (int i = 0; i < roleCount; i++)
        {
            int recordLength = reader.ReadInt32();
            long recordEnd = reader.BaseStream.Position + recordLength;

            var role = new RoleDefinition { Name = reader.ReadString() };
            int permissionCount = reader.ReadInt32();
            for (int j = 0; j < permissionCount; j++)
                role.Permissions.Add(reader.ReadString());

            // неизвестные поля хвоста записи — пропустить
            reader.BaseStream.Position = recordEnd;
            roles.Add(role);
        }

        return new UsersConfiguration
        {
            IsConfigured = true, // секция есть — значит в проекте был roles.json
            Roles = roles,
            MinPasswordLength = reader.ReadInt32(),
            IdleTimeoutMinutes = reader.ReadInt32()
        };
    }
}
