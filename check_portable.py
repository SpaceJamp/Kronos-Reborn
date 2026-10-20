import re

path = r'C:\Users\Yeti\Downloads\Kronos-Reborn-main\Kronos-Reborn-main\src\KronosReborn\UI\PortableViewModels.cs'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Find class/struct/enum declarations
for m in re.finditer(r'(public|internal|private)\s+(sealed\s+)?(class|struct|enum|interface|record)\s+(\w+)', content):
    print(f'{m.group(1)} {m.group(2) or ""}{m.group(3)} {m.group(4)}')

# Check if ViewMode enum is used elsewhere in ViewModels.cs
vm_path = r'C:\Users\Yeti\Downloads\Kronos-Reborn-main\Kronos-Reborn-main\src\KronosReborn\UI\ViewModels.cs'
with open(vm_path, 'r', encoding='utf-8') as f:
    vm_content = f.read()

print()
print('ViewMode references in ViewModels.cs:')
for i, line in enumerate(vm_content.split('\n'), 1):
    if 'ViewMode' in line:
        print(f'  {i}: {line.strip()}')
