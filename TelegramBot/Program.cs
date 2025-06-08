using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TelegramBotTask
{
	class Program
	{
		static HttpClient HttpClient = new HttpClient();

		static string lastCommand = null;
		static string TelegramBotToken, ApiToken;
		static TelegramBotClient bot;
		static User me;
		static bool IsAwaitingINN = false;
		static async Task Main()
		{
			LoadTokens(); //получаем токены для бота и API из конфигурационного файла
			using var cts = new CancellationTokenSource();
			bot = new TelegramBotClient(TelegramBotToken, cancellationToken: cts.Token);
			me = await bot.GetMe(); //проверка токена бота


			bot.OnMessage += OnMessage;
			bot.OnError += OnError;

			Console.WriteLine($"@{me.Username} is running... Press Enter to terminate"); // вывод в консоли сообщения о работе бота
			Console.ReadLine();
			cts.Cancel(); // остановка бота
		}

		//метод для получения токенов телеграм бота и АПИ ФНС
		static void LoadTokens()
		{
			var configText = File.ReadAllText("config.json"); //конфигурационный файл с токенами
			dynamic config = JsonConvert.DeserializeObject(configText);
			TelegramBotToken = config.TelegramBotToken;
			ApiToken = config.FnsApiToken;
		}

		static async Task OnError(Exception exception, HandleErrorSource source)
		{
			Console.WriteLine(exception);
		}

		//получение сообщения
		static async Task OnMessage(Message msg, UpdateType type)
		{
			if (msg.Text is not { } text)
			{
				await bot.SendMessage(msg.Chat, $"Бот работает ТОЛЬКО с текстовыми командами. Содержимое отправленного вами сообщения включает в себя {msg.Type}");

				Console.WriteLine($"Recevied a message of type {msg.Type}");
			}
			else if (text.StartsWith('/')) //команды начинаются со знака /
			{
				//ищем позицию первого пробела в строке
				//если пробел не найден, то значит вся строка является командой
				var space = text.IndexOf(' ');
				if (space < 0) space = text.Length;

				//переводим команду в нижний регистр
				var command = text[..space].ToLower();

				//целевая команда, на случай использования в групповом чате с другими ботами
				if (command.LastIndexOf('@') is > 0 and int at) // проверка команды
					if (command[(at + 1)..].Equals(me.Username, StringComparison.OrdinalIgnoreCase))
						command = command[..at];
					else
						return; // игнорирование команды
				await OnCommand(command, text[space..].TrimStart(), msg);
			}
			else
			{
				await OnTextMessage(msg);
			}
		}

		//получение команд
		static async Task OnCommand(string command, string args, Message msg)
		{
			switch (command)
			{
				case "/start":
					await bot.SendMessage(msg.Chat, "Здравствуйте. Это бот, который может вывести информацию о компании по введенному ИНН. " +
						"Для получения справки введите команду /help");
					lastCommand = command;
					IsAwaitingINN = false;
					break;

				case "/hello":
					string filePath = "hello.txt";
					if (File.Exists(filePath))
					{
						string messageText = await File.ReadAllTextAsync(filePath);
						await bot.SendMessage(msg.Chat, $"Для отображения полной информации, перейдите по ссылке на резюме \n{messageText}");
					}
					else
					{
						await bot.SendMessage(msg.Chat, "Не удалось найти файл с информацией");

					}
					lastCommand = command;
					IsAwaitingINN = false;
					break;

				case "/help":
					await bot.SendMessage(msg.Chat, "Доступные команды: \n /start - начало работы боты \n " +
						"/hello - вывод информации об авторе \n " +
						"/inn - вывод информации о компании по введенному inn. Вы можете ввести несколько ИНН, через точку с запятой. ИНН компаний состоит из 10 цифр \n" +
						"/last - повторное выполнение последней введенной команды \n" +
						"/help - вывод справки о доступных командах");
					lastCommand = command;
					IsAwaitingINN = false;
					break;

				case "/inn":
					IsAwaitingINN = true;
					await bot.SendMessage(msg.Chat, "Для проверки данных компании по ИНН введите число");
					lastCommand = command;
					break;

				case "/last":
					if (lastCommand != null)
					{
						command = lastCommand;
						await OnCommand(command, args, msg);
					}
					else
					{
						await bot.SendMessage(msg.Chat, "Бот только что запущен. Нет команды для выполнения");
					}
					break;

				default:
					await bot.SendMessage(msg.Chat, "Команда не распознана. Для получения списка доступных команд введите /help");
					break;
			}
		}

		//получение текствого сообщения
		static async Task OnTextMessage(Message msg)
		{
			if (msg.Text is null)
			{
				return;
			}

			if (IsAwaitingINN == false)
			{
				await bot.SendMessage(msg.Chat, "Вы ввели текстовое сообщение. На данный момент бот умеет работать только с определёнными командами. Для получения списка доступных комманд введите /help");
			}
			else
			{
				string[] numbers = msg.Text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				int count = numbers.Length;

				foreach (string number in numbers)
				{
					string trimmedNumber = number.Trim();
					if (CheckInnNumber(trimmedNumber) == true && trimmedNumber.Length == 10)
					{
						var companies = await GetCompanyInfo(trimmedNumber, msg);
					}
					else
					{
						await bot.SendMessage(msg.Chat.Id, "Введен некорректный ИНН, повторите снова.");
					}
				}
				var sortedCompanies = companies.OrderBy(c => c.CompanyName, StringComparer.Create(new System.Globalization.CultureInfo("ru-RU"), true)).ToList();

				foreach (var comp in sortedCompanies)
				{
					await bot.SendMessage(msg.Chat, $"Название: {comp.CompanyName}\n" +
						$"ИНН: {comp.CompanyINN}\n" +
						$"Адрес: {comp.CompanyAddress}");
				}
				companies.Clear();
			}
		}

		//метод для проверки того, что введенный ИНН является числом
		static bool CheckInnNumber(string inn)
		{
			foreach (char c in inn)
			{
				if (!char.IsDigit(c))
					return false;
			}
			return true;
		}

		//список, в него помещаем компании, полученные в результате запроса к АПИ
		static List<Company> companies = new List<Company>();

		static async Task<List<Company>> GetCompanyInfo(string inn, Message msg)
		{
			string url = "https://api-fns.ru/api/search"; //ссылка на АПИ ФНС
			try
			{
				var response = await HttpClient.GetAsync($"{url}?q={inn}&key={ApiToken}");
				if (response.IsSuccessStatusCode)
				{
					var jsonString = await response.Content.ReadAsStringAsync();
					var parsedJson = JObject.Parse(jsonString);
					var itemsArray = parsedJson["items"] as JArray;

					var item = itemsArray[0]; //первый элемент массива это нужная компания
					Company company = new Company
					{
						//получение доступа к данным, через ключ ЮЛ
						CompanyAddress = item["ЮЛ"]?["АдресПолн"]?.ToString() ?? "Адрес не найден",
						CompanyName = item["ЮЛ"]?["НаимСокрЮЛ"]?.ToString() ?? "Наименование не найдено",
						CompanyINN = item["ЮЛ"]?["ИНН"]?.ToString() ?? "ИНН не найден"
					};
					if (company.CompanyINN == inn)
					{
						companies.Add(company);
					}
					else
					{
						await bot.SendMessage(msg.Chat, "ИНН не найден");
					}
				}
				else
				{
					await bot.SendMessage(msg.Chat, "Не удалось подключиться к API");
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка при запросе API: {ex.Message}");
				await bot.SendMessage(msg.Chat, ex.Message.ToString());
			}
			return companies;
		}
	}
}