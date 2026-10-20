import sys

path = r'C:\Users\Yeti\Downloads\Kronos-Reborn-main\Kronos-Reborn-main\src\KronosReborn\MainWindow.xaml'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Fix card width from 200 to 240
content = content.replace('<Setter Property="Width" Value="200" />', '<Setter Property="Width" Value="240" />')
# Fix margin from 8 to 10
content = content.replace('<Setter Property="Margin" Value="8" />', '<Setter Property="Margin" Value="10" />')
# Fix poster height from 140 to 160
content = content.replace('<RowDefinition Height="140" />', '<RowDefinition Height="160" />')
# Fix title margin
content = content.replace('Margin="8,4,8,0"', 'Margin="10,6,10,0"')
# Fix stackpanel margin
content = content.replace('Margin="8,4,8,8"', 'Margin="10,6,10,8"')
# Fix poster font size
content = content.replace('FontSize="48"', 'FontSize="64"')
# Fix title max height
content = content.replace('MaxHeight="24"', 'MaxHeight="30"')

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)

print('Done')
