using RadioSipGateway.Linux.Configuration;
using RadioSipGateway.Linux.Sip;
using RadioSipGateway.Linux.Radio;

namespace RadioSipGateway.Linux;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string configPath = "/etc/radiosipgateway/config.ini";
        bool checkOnly = false;
        bool selfTestDtmf = false;

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
                case "--self-test-dtmf":
                    selfTestDtmf = true;
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
            if (selfTestDtmf) return DtmfDecoderSelfTest.Run(Log);

            GatewayConfig config = IniConfigLoader.Load(configPath);
            ConfigValidator.Validate(config);
            Console.WriteLine($"Конфигурация прочитана: {Path.GetFullPath(configPath)}");
            Console.WriteLine($"SIP-сервер: {config.Sip.Server}:{config.Sip.Port}");
            Console.WriteLine($"SIP test auto-answer: {(config.Sip.AutoAnswerForRtpTest ? "ВКЛЮЧЁН" : "выключен")}");
            Console.WriteLine($"PTT: {config.Transmit.Mode}; VOX: {(config.Transmit.VoxEnabled ? "включён" : "выключен")}");
            Console.WriteLine($"ALSA capture: {config.Audio.CaptureDevice}; playback: {config.Audio.PlaybackDevice}; bridge: {(config.Audio.BridgeEnabled ? "ВКЛЮЧЁН" : "выключен")}");

            if (checkOnly)
            {
                Console.WriteLine("Проверка конфигурации завершена успешно.");
                return 0;
            }

            Console.WriteLine(config.Sip.AutoAnswerForRtpTest
                ? config.Audio.BridgeEnabled
                ? "ТЕСТОВЫЙ РЕЖИМ: входящие вызовы будут отвечены с двусторонним ALSA/RTP-аудиомостом. CM108 PTT не подключён; передатчик не включается."
                : "ТЕСТОВЫЙ РЕЖИМ: входящие вызовы будут отвечены тестовой RTP-медиасессией с тишиной. ALSA-аудиомост и CM108 PTT не подключены."
                : "РЕЖИМ SIP-регистрации: входящие вызовы отклоняются, если SIP.AutoAnswerForRtpTest=false. ALSA-аудиомост и CM108 PTT не подключены.");
            Console.WriteLine("Для остановки нажмите Ctrl+C.");
            using var stopSignal = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                stopSignal.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            try
            {
                await using var sip = new SipRegistrationService(config.Sip, config.Audio, Log);
                sip.Start();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, stopSignal.Token);
                }
                catch (OperationCanceledException) when (stopSignal.IsCancellationRequested) { }
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            Console.WriteLine("Завершение работы.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 1;
        }
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}");

    private static void PrintHelp()
    {
        Console.WriteLine("RadioSipGateway Linux — консольный SIP/радиошлюз");
        Console.WriteLine("Использование:");
        Console.WriteLine("  RadioSipGateway --check-config [--config ПУТЬ]");
        Console.WriteLine("  RadioSipGateway --self-test-dtmf");
        Console.WriteLine("  RadioSipGateway --config ПУТЬ");
    }
}
