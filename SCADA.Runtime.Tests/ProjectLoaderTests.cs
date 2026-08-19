using SCADA.Runtime.Configuration;
using SCADA.Runtime.Historian;

namespace SCADA.Runtime.Tests;

public class ProjectLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public ProjectLoaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, name), content);

    // Валидный набор файлов: тесты на ошибки перезаписывают один из них кривым
    private void WriteValidProject()
    {
        WriteFile("project.json", """
            {"formatVersion": 1, "name": "TestProject", "version": "1.0"}
            """);
        WriteFile("devices.json", """
            {
              "formatVersion": 1,
              "channels": [{"id": 0, "name": "Ch0", "channelType": "modbus-tcp"}],
              "devices": [{"id": 0, "name": "PLC0", "driverName": "simulator", "channelId": 0}]
            }
            """);
        WriteFile("tags.json", """
            {
              "formatVersion": 1,
              "tags": [
                {"id": 0, "name": "T1", "dataType": "analog", "deviceId": 0},
                {"id": 1, "name": "T2", "dataType": "discrete", "deviceId": 0}
              ]
            }
            """);
    }

    [Fact]
    public void Load_ValidProject_ReturnsConfiguration()
    {
        WriteValidProject();

        var config = ProjectLoader.Load(_dir);

        Assert.Equal("TestProject", config.Name);
        Assert.Equal("1.0", config.Version);

        // загрузчик добавляет диагностику канала (§7.4): 1 устройство + 7 тегов,
        // диагностику архива (§7.5): 1 устройство + свой набор метрик,
        // и системные теги аварий (A5): псевдодевайс "@Alarms" + корневые метрики
        Assert.Equal(2 + 7 + ArchiveDiagnostics.MetricDefinitions.Count + SCADA.Core.Alarms.AlarmTags.SystemMetrics.Length,
            config.Tags.Count);
        Assert.Equal(4, config.Devices.Count);
        Assert.Single(config.Channels);
        Assert.Equal("@Ch0", config.Devices[1].Name);
        Assert.Equal("@Ch0.Connected", config.Tags[2].Name);
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        WriteValidProject();
        File.Delete(Path.Combine(_dir, "tags.json"));

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("Не найден файл tags.json"));
    }

    [Fact]
    public void Load_InvalidJson_Throws()
    {
        WriteValidProject();
        WriteFile("tags.json", """{"formatVersion": 1, "tags": [""");

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("tags.json") && e.Contains("ошибка JSON"));
    }

    [Fact]
    public void Load_WrongFormatVersion_Throws()
    {
        WriteValidProject();
        WriteFile("devices.json", """
            {
              "formatVersion": 99,
              "channels": [{"id": 0, "name": "Ch0", "channelType": "modbus-tcp"}],
              "devices": [{"id": 0, "name": "PLC0", "driverName": "simulator", "channelId": 0}]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("devices.json") && e.Contains("99"));
    }

    [Fact]
    public void Load_TagWithUnknownDevice_Throws()
    {
        WriteValidProject();
        WriteFile("tags.json", """
            {
              "formatVersion": 1,
              "tags": [{"id": 0, "name": "T1", "dataType": "analog", "deviceId": 5}]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("T1") && e.Contains("не найдено"));
    }

    [Fact]
    public void Load_DeviceWithUnknownChannel_Throws()
    {
        WriteValidProject();
        WriteFile("devices.json", """
            {
              "formatVersion": 1,
              "channels": [],
              "devices": [{"id": 0, "name": "PLC0", "driverName": "simulator", "channelId": 7}]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("PLC0") && e.Contains("не найден"));
    }

    [Fact]
    public void Load_DuplicateTagId_Throws()
    {
        WriteValidProject();
        WriteFile("tags.json", """
            {
              "formatVersion": 1,
              "tags": [
                {"id": 0, "name": "T1", "dataType": "analog", "deviceId": 0},
                {"id": 0, "name": "T2", "dataType": "analog", "deviceId": 0}
              ]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("Дубликат TagId 0"));
    }

    [Fact]
    public void Load_TagIdGap_Throws()
    {
        WriteValidProject();
        WriteFile("tags.json", """
            {
              "formatVersion": 1,
              "tags": [
                {"id": 0, "name": "T1", "dataType": "analog", "deviceId": 0},
                {"id": 2, "name": "T2", "dataType": "analog", "deviceId": 0}
              ]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("не покрывают диапазон"));
    }

    [Fact]
    public void Load_MultipleErrors_ReportsAll()
    {
        WriteValidProject();
        File.Delete(Path.Combine(_dir, "tags.json"));
        WriteFile("devices.json", """{"formatVersion": 1, "devices": [""");

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Equal(2, ex.Errors.Count);
    }
}
