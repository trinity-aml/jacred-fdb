# Установка
curl -s https://raw.githubusercontent.com/immisterio/jacred-fdb/main/install.sh | bash

# Источники 
Kinozal, Nnmclub, Rutor, Torrentby, Bitru, Rutracker, Megapeer, Selezen, Toloka (UKR), Rezka, Baibako, LostFilm, Anilibria, Animelayer, Anifilm

# Доступ к доменам .onion
1. Запустить tor на порту 9050
2. В init.conf указать .onion домен в host

# Настройка init.conf
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
example.conf

```
{
  "listenip": "any", // 127.0.0.1
  "listenport": 9117,
  "apikey": "",
  "mergeduplicates": true,
  "openstats": true,
  "opensync": false,
  "evercache": false,
  "timeStatsUpdate": 20,
  "timeSync": 10,
  "syncapi": "",
  "Rutor": {
    "useproxy": false,
	"reqMinute": 14
  },
  "Megapeer": {
    "useproxy": false,
	"reqMinute": 14
  },
  "TorrentBy": {
    "useproxy": false,
	"reqMinute": 14
  },
  "Bitru": {
    "useproxy": false,
	"reqMinute": 14
  },
  "NNMClub": {
    "useproxy": false,
	"reqMinute": 14,
	//"alias": "http://nnmclub2vvjqzjne6q4rrozkkkdmlvnrcsyes2bbkm7e5ut2aproy4id.onion"
  },
  "Anilibria": {
    "useproxy": false,
	"reqMinute": 14
  },
  "Anifilm": {
    "useproxy": false,
	"reqMinute": 14
  },
  "Rezka": {
    "useproxy": false,
	"reqMinute": 14
  },
  "Kinozal": {
    "useproxy": false,
	"reqMinute": 14
	"login": {
      "u": "",
      "p": ""
    }
  },
  "Toloka": {
    "useproxy": false,
	"reqMinute": 14
    "login": {
      "u": "",
      "p": ""
    }
  },
  "Rutracker": {
    "useproxy": false,
	"reqMinute": 14
    "login": {
      "u": "",
      "p": ""
    }
  },
  "Selezen": {
    "useproxy": false,
	"reqMinute": 14
    "login": {
      "u": "",
      "p": ""
    }
  },
  "Animelayer": {
    "useproxy": false,
	"reqMinute": 14
    "login": {
      "u": "",
      "p": ""
    }
  },
  "Baibako": {
    "useproxy": false,
	"reqMinute": 14
    "login": {
      "u": "",
      "p": ""
    }
  },
  "Lostfilm": {
    "useproxy": false,
	"reqMinute": 14
    "cookie": ""
  },
  "proxy": {
    "useAuth": false,
    "BypassOnLocal": false,
    "username": "",
    "password": "",
    "list": [
      "ip:port",
      "socks5://ip:port"
	]
  },
  "globalproxy": [
	{
      "pattern": "\\.onion",
      "list": [
        "socks5://127.0.0.1:9050"
      ]
    }
  ]
}
```
