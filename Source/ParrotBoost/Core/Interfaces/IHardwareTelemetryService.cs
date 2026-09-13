using System.Threading.Tasks;

namespace ParrotBoost.Core.Interfaces
{
    public interface IHardwareTelemetryService
    {
        float GetAccurateCpuLoad();
        float GetAccurateGpuLoad();
        float GetAccurateCpuTemp();
        float GetAccurateGpuTemp(float load);
        Task RunDiagnosticScanAsync();
    }
}