using System.Text;
using SCADA.Core.Channels;
using SCADA.Core.Devices;

namespace SCADA.Package.Builder.Sections;

/// <summary>
/// Сериализатор секции devices.bin: сначала каналы, потом устройства
/// (устройства ссылаются на каналы по Id). Зеркален DevicesSectionReader.
/// Правила раскладки — как в TagsSectionWriter.
/// </summary>
public static class DevicesSectionWriter
{
    public static byte[] Write(IReadOnlyList<ChannelDefinition> channels,
        IReadOnlyList<DeviceDefinition> devices)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        writer.Write(channels.Count);
        foreach (var channel in channels)
            WriteRecord(writer, r =>
            {
                r.Write(channel.Id.Value);
                r.Write(channel.Name);
                r.Write(channel.Description);
                r.Write(channel.ChannelType);
                r.Write(channel.Configuration);
            });

        writer.Write(devices.Count);
        foreach (var device in devices)
            WriteRecord(writer, r =>
            {
                r.Write(device.Id.Value);
                r.Write(device.Name);
                r.Write(device.Description);
                r.Write(device.ChannelId.Value);
                r.Write(device.DriverName);
                r.Write(device.Configuration);
            });

        writer.Flush();
        return stream.ToArray();
    }

    // запись собирается в отдельный поток, чтобы узнать её длину
    private static void WriteRecord(BinaryWriter writer, Action<BinaryWriter> writeFields)
    {
        using var recordStream = new MemoryStream();
        using (var record = new BinaryWriter(recordStream, Encoding.UTF8, leaveOpen: true))
        {
            writeFields(record);
            record.Flush();
        }

        writer.Write((int)recordStream.Length);
        writer.Write(recordStream.GetBuffer(), 0, (int)recordStream.Length);
    }
}
