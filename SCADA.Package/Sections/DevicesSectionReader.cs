using System.Text;
using SCADA.Core.Channels;
using SCADA.Core.Devices;

namespace SCADA.Package.Sections;

/// <summary>
/// Читатель секции devices.bin. Зеркален DevicesSectionWriter:
/// сначала каналы, потом устройства. Хвосты записей пропускаются
/// через длину — как в TagsSectionReader.
/// </summary>
public static class DevicesSectionReader
{
    public static (IReadOnlyList<ChannelDefinition> Channels, IReadOnlyList<DeviceDefinition> Devices)
        Read(byte[] section)
    {
        using var stream = new MemoryStream(section);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        int channelCount = reader.ReadInt32();
        var channels = new List<ChannelDefinition>(channelCount);
        for (int i = 0; i < channelCount; i++)
        {
            channels.Add(ReadRecord(reader, stream, r => new ChannelDefinition
            {
                Id = new ChannelId(r.ReadInt32()),
                Name = r.ReadString(),
                Description = r.ReadString(),
                ChannelType = r.ReadString(),
                Configuration = r.ReadString()
            }));
        }

        int deviceCount = reader.ReadInt32();
        var devices = new List<DeviceDefinition>(deviceCount);
        for (int i = 0; i < deviceCount; i++)
        {
            devices.Add(ReadRecord(reader, stream, r => new DeviceDefinition
            {
                Id = new DeviceId(r.ReadInt32()),
                Name = r.ReadString(),
                Description = r.ReadString(),
                ChannelId = new ChannelId(r.ReadInt32()),
                DriverName = r.ReadString(),
                Configuration = r.ReadString()
            }));
        }

        return (channels, devices);
    }

    // читает длину записи, отдаёт поля делегату, хвост пропускает
    private static T ReadRecord<T>(BinaryReader reader, MemoryStream stream,
        Func<BinaryReader, T> readFields)
    {
        int recordLength = reader.ReadInt32();
        long recordEnd = stream.Position + recordLength;

        var result = readFields(reader);

        stream.Position = recordEnd;
        return result;
    }
}
