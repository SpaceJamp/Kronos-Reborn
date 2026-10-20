import os
base = r'C:\Users\Yeti\Downloads\Kronos-Reborn-main\Kronos-Reborn-main'
files = [
    'src/KronosReborn/Data/Runtimes/RuntimeImporter.cs',
]
for f in files:
    path = os.path.join(base, f)
    with open(path, 'r') as fh:
        content = fh.read()
    if 'KronosReborn.Data.Logging' in content:
        print(f'{f}: Already has Logging import')
    else:
        # Add logging import after System.Linq
        content = content.replace(
            'using System.Linq;\nusing KronosReborn.Data.Persistence;',
            'using System.Linq;\nusing KronosReborn.Data.Logging;\nusing KronosReborn.Data.Persistence;'
        )
        with open(path, 'w') as fh:
            fh.write(content)
        print(f'{f}: Added Logging import')