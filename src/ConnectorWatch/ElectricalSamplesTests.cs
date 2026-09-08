using System.Text.Json;

namespace ConnectorWatch;

/// <summary>Offline contract tests for typed electrical samples.  They use
/// synthetic decoded values and never construct a native provider.</summary>
public static class ElectricalSamplesTests
{
    public static void Run()
    {
        Check(AnalysisLoadSource.CONNECTOR_CURRENT.WireName() == "CONNECTOR_CURRENT",
            "analysis source wire name");
        Check(AnalysisLoadSourceExtensions.TryParse("connector-power", out var parsed) &&
            parsed == AnalysisLoadSource.CONNECTOR_POWER, "analysis source parser");
        Check(!AnalysisLoadSourceExtensions.TryParse("0", out _), "numeric analysis source rejected");
        Check(JsonSerializer.Serialize(AnalysisLoadSource.CONNECTOR_POWER) == "\"CONNECTOR_POWER\"",
            "analysis source serializes by stable wire name");

        var timestamp = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);
        var native = new DirectRailSample(timestamp, 0x7ABF,
            new DirectRailChannelSample(1, 255, 453, 12_045_549,
                12.045549, .453, 5.456633697, timestamp)
            { RawStatusDword28 = 22_396_125 },
            new DirectRailChannelSample(2, 218, 5_544, 12_030_060,
                12.030060, 5.544, 66.69465264, timestamp)
            { RawStatusDword28 = 219_231_926 });
        var direct = DirectNvRails.ToElectricalSample(native, "fixture direct rails");
        Check(direct.Connector.VoltageV == 12.030060 && direct.Connector.CurrentA == 5.544,
            "direct connector typed V/A");
        Check(direct.Connector.PowerW == 66.69465264 &&
            direct.Connector.PowerProvenance == ElectricalPowerProvenance.DerivedFromVoltageAndCurrent,
            "direct connector derived power provenance");
        Check(direct.Pcie12V.VoltageV == 12.045549 && direct.Pcie12V.CurrentA == .453,
            "direct PCIe typed V/A");
        Check(direct.ExternalPower.Power.Availability == ElectricalFieldAvailability.Unsupported,
            "direct external power remains unsupported");
        using (var extras = JsonDocument.Parse(direct.RawExtras))
        {
            Check(extras.RootElement.GetProperty("12vhpwr_raw_status_dword_28").GetUInt32() == 219_231_926,
                "direct raw extras retained");
        }
        var legacyDirect = direct.ToLegacyVoltage();
        Check(legacyDirect.Volts == direct.Connector.VoltageV &&
            legacyDirect.Power == direct.Connector.PowerW && legacyDirect.Extras == direct.RawExtras,
            "direct legacy projection retained");
        var serializedLegacy = JsonSerializer.Serialize(legacyDirect);
        var deserializedLegacy = JsonSerializer.Deserialize<Voltage>(serializedLegacy)!;
        Check(deserializedLegacy == legacyDirect, "legacy Voltage JSON round-trip retained");
        Check(HybridStorage.Header.StartsWith(
            "timestamp_utc,gpu_uuid,board_power_w,input_voltage_v,voltage_timestamp_utc,analysis_power_w,analysis_power_source,gpu_temp_c,utilization_pct,power_limit_w,voltage_source,extra_voltages_json,bin_w,reference_v,rolling_median_v,rolling_p05_v,median_drop_v,status,detail,",
            StringComparison.Ordinal), "legacy CSV columns retain their order");

        var fresh = FreshnessMetadata.SourceTimestamp(timestamp,
            timestamp.AddSeconds(2), 5, "fixture external timestamp");
        var legacyExternal = new Voltage(timestamp, 12.1, 440.5,
            "{\"12vhpwr_a\":999,\"pcie_12v_v\":12.0}");
        var external = ElectricalSample.FromLegacy(legacyExternal, "fixture external",
            fresh, powerIsExternalSensor: true);
        Check(!external.Connector.HasCurrent &&
            external.Connector.Current.Availability == ElectricalFieldAvailability.Unsupported,
            "legacy external current is explicitly unsupported");
        Check(!external.Connector.HasPower && external.ExternalPower.PowerW == 440.5,
            "external power is not reclassified as connector power");
        Check(external.RawExtras == legacyExternal.Extras, "legacy extras retained verbatim");
        Check(!external.SelectAnalysisLoad(AnalysisLoadSource.CONNECTOR_POWER).IsAvailable,
            "missing connector power does not fall back");
        var selectedExternal = external.SelectAnalysisLoad(AnalysisLoadSource.EXTERNAL_SENSOR_POWER);
        Check(selectedExternal.IsAvailable && selectedExternal.Watts == 440.5,
            "external sensor power selection");

        var connectorFresh = FreshnessMetadata.HostPoll(timestamp, "fixture host poll");
        var connector = ElectricalRailSample.Create(12.1, 36.4, 440.44,
            ElectricalPowerProvenance.DerivedFromVoltageAndCurrent, "fixture",
            timestamp, connectorFresh);
        var pcie = ElectricalRailSample.Unsupported("fixture", timestamp, connectorFresh,
            "fixture does not expose PCIe");
        var typed = new ElectricalSample(timestamp, "fixture", connector, pcie,
            ElectricalPowerReading.Unsupported("fixture", timestamp, connectorFresh),
            connectorFresh, "legacy extras");
        var current = typed.SelectAnalysisLoad(AnalysisLoadSource.CONNECTOR_CURRENT);
        Check(current.IsAvailable && current.Amps == 36.4 && current.Unit == "A",
            "connector current selection");
        var derivedWatts = current.ToAnalysisPowerWatts(typed);
        Check(derivedWatts.HasValue && Math.Abs(derivedWatts.Value - 440.44) < 0.000001,
            "connector current is converted to the watt-binned load with same-sample voltage");
        var missingVoltage = typed with
        {
            Connector = typed.Connector with { Voltage = ElectricalMeasurement.Missing("V") }
        };
        Check(current.ToAnalysisPowerWatts(missingVoltage) is null,
            "current basis without same-sample voltage does not fall back");
        Check(typed.SelectAnalysisLoad(AnalysisLoadSource.EXTERNAL_SENSOR_POWER).IsAvailable == false,
            "source switch stays unavailable");

        var board = typed.SelectAnalysisLoad(AnalysisLoadSource.NVML_BOARD_POWER, 451.2,
            FreshnessMetadata.HostPoll(timestamp, "fixture NVML board power"));
        Check(board.IsAvailable && board.Watts == 451.2 && board.SourceName == "NVML_BOARD_POWER",
            "NVML board power selection");
        Check(!typed.SelectAnalysisLoad(AnalysisLoadSource.NVML_BOARD_POWER).IsAvailable,
            "missing NVML board power stays unavailable");

        var legacyConfig = new Config();
        Check(Program.ResolveAnalysisLoadSource(legacyConfig, "direct", null) ==
            AnalysisLoadSource.CONNECTOR_POWER, "legacy direct config resolves once to connector power");
        Check(Program.ResolveAnalysisLoadSource(legacyConfig, "hwinfo", null) ==
            AnalysisLoadSource.NVML_BOARD_POWER, "legacy external config without paired power resolves to NVML");
        legacyConfig.AnalysisLoadSource = "EXTERNAL_SENSOR_POWER";
        Check(Program.ResolveAnalysisLoadSource(legacyConfig, "direct", null) ==
            AnalysisLoadSource.EXTERNAL_SENSOR_POWER, "explicit analysis source overrides provider default");

        var stale = FreshnessMetadata.SourceTimestamp(timestamp,
            timestamp.AddSeconds(6), 5, "fixture stale timestamp");
        var staleRail = ElectricalRailSample.Create(12.1, 36.4, 440.44,
            ElectricalPowerProvenance.DerivedFromVoltageAndCurrent, "fixture",
            timestamp, stale);
        var staleSample = typed with { Connector = staleRail, Freshness = stale };
        Check(!staleSample.SelectAnalysisLoad(AnalysisLoadSource.CONNECTOR_CURRENT).IsAvailable,
            "stale selected source unavailable");

        bool nonFiniteRejected = false;
        try
        {
            _ = ElectricalRailSample.Create(12, double.NaN, null,
                ElectricalPowerProvenance.Unavailable, "fixture", timestamp,
                connectorFresh, powerAvailability: ElectricalFieldAvailability.Unsupported);
        }
        catch (ArgumentOutOfRangeException) { nonFiniteRejected = true; }
        Check(nonFiniteRejected, "non-finite typed electrical field rejected");

        Console.WriteLine("PASS: typed electrical samples, provenance, freshness and load-source fixtures.");
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }
}
