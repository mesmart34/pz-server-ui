# PZ Server UI

Локальное приложение Windows для управления существующими выделенными серверами Project Zomboid. Стек: .NET 10 и .NET MAUI с нативным XAML.

## Что уже реализовано

Можно выбрать папку существующей установки Dedicated Server. Панель проверяет наличие одного из стандартных файлов запуска (`StartServer64.bat`, `StartServer64_nosteam.bat` или `StartServer32.bat`) и запоминает выбор. Игру и сервер она не запускает. Поиск профилей относится к следующей задаче.

## Сборка и проверка

Для сборки на Windows нужны .NET 10 SDK и workload `maui-windows`. Установить workload на машине разработки:

```powershell
dotnet workload install maui-windows
```

Собрать и запустить приложение:

```powershell
dotnet build
dotnet run --project src/PzServerUi
```

Проверить сценарии выбора установки можно без MAUI и установки игры:

```powershell
dotnet run --project tests/PzServerUi.Application.Tests
```

- [Спецификация первой версии](https://github.com/mesmart34/pz-server-ui/issues/20)
- [Доска проекта](https://github.com/users/mesmart34/projects/6)
- [Задачи и зависимости](https://github.com/mesmart34/pz-server-ui/issues)
- [Глоссарий проекта](CONTEXT.md)
