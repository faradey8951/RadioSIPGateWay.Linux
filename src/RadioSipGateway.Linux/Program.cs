using RadioSipGateway.Linux.Configuration;
using RadioSipGateway.Linux.Diagnostics;

namespace RadioSipGateway.Linux;

internal static class Program
{
    private static int Main(string[] args)
    {
        string configPath = "/etc/radiosipgateway/config.ini";
        bool checkOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--check-config":
                    checkOnly = true;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"Неизвестный аргумент: {args[i]}");
                    PrintHelp();
                    return 2;
            }
        }

        try
        {
            GatewayConfig config = IniConfigLoader.Load(configPath);
            ConfigValidator.Validate(config);
            Console.WriteLine($"Конфигурация прочитана: {Path.GetFullPath(configPath)}");
            Console.WriteLine($"SIP-сервер: {config.Sip.Server}:{config.Sip.Port}");
            Console.WriteLine($"PTT: {config.Transmit.Mode}; VOX: {(config.Transmit.VoxEnabled ? "включён" : "выключен")}");
            Console.WriteLine($"ALSA capture: {config.Audio.CaptureDevice}; playback: {config.Audio.PlaybackDevice}");

            if (checkOnly)
            {
                Console.WriteLine("Проверка конфигурации завершена успешно.");
                return 0;
            }

            Console.Error.WriteLine("Linux-порт пока находится в стадии переноса: SIP-регистрация, ALSA-аудиотракт и CM108 PTT ещё не подключены.");
            Console.Error.WriteLine("Чтобы не создавать ложное впечатление работающего шлюза, запуск передачи на этом этапе заблокирован.");
            Console.Error.WriteLine("Сейчас доступен только --check-config. См. docs/PORTING_STATUS_RU.md.");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("RadioSipGateway Linux — консольный SIP/радиошлюз");
        Console.WriteLine("Использование:");
        Console.WriteLine("  RadioSipGateway --check-config [--config ПУТЬ]");
        Console.WriteLine("  RadioSipGateway --config ПУТЬ");
    }
}
