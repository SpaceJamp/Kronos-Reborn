import os
base = r'C:\Users\Yeti\Downloads\Kronos-Reborn-main\Kronos-Reborn-main'
files = [
    'src/KronosReborn/Data/Downloads/NgxDownloader.cs',
    'src/KronosReborn/Data/Actions/SwapEngine.cs',
    'src/KronosReborn/Data/Runtimes/RuntimeImporter.cs',
    'src/KronosReborn/MainWindow.xaml.cs',
    'src/KronosReborn/UI/NgxDownloadViewModel.cs',
]
for f in files:
    path = os.path.join(base, f)
    with open(path, 'r') as fh:
        content = fh.read()
    content = content.replace('Logger.Instance.', 'Log.Instance.')
    with open(path, 'w') as fh:
        fh.write(content)
    print(f'Updated {f}')