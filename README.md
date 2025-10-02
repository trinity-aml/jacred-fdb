# Источники 
Kinozal, Nnmclub, Rutor, Torrentby, Bitru, Rutracker, Megapeer, Selezen, Toloka (UKR), Baibako, LostFilm, Animelayer, Anilibria, Anifilm, Anistar

# Самостоятельный парсинг источников
1. Настроить init.conf (пример настроек в example.conf)
2. Перенести в crontab "Data/crontab" или указать сервер "syncapi" в init.conf 

# Доступ к доменам .onion
1. Запустить tor на порту 9050
2. В init.conf указать .onion домен в host

# Параметры init.conf
* apikey - включение авторизации по ключу
* mergeduplicates - объединять дубликаты в выдаче 
* openstats - открыть доступ к статистике 
* opensync - разрешить синхронизацию с базой через syncapi
* syncapi - источник с открытым opensync для синхронизации базы 
* timeSync - интервал синхронизации с базой syncapi 
* maxreadfile - максимальное количество открытых файлов за один поисковый запрос 
* evercache - хранить открытые файлы в кеше (рекомендуется для общего доступа с высокой нагрузкой)
* timeStatsUpdate - интервал обновления статистики в минутах 


# Пример init.conf
* Список всех параметров, а так же значения по умолчанию смотреть в example.conf 
* В init.conf нужно указывать только те параметры, которые хотите изменить

```
{
  "listenport": 9120, // изменили порт
  "NNMClub": {        // изменили домен на адрес из сети tor 
    "alias": "http://nnmclub2vvjqzjne6q4rrozkkkdmlvnrcsyes2bbkm7e5ut2aproy4id.onion"
  },
  "globalproxy": [
    {
      "pattern": "\\.onion",  // запросы на домены .onion отправить через прокси
      "list": [
        "socks5://127.0.0.1:9050" // прокси сервер tor
      ]
    }
  ]
}
```
