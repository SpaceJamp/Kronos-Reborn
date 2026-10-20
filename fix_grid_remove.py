import sys

path = r'C:\Users\Yeti\Downloads\Kronos-Reborn-main\Kronos-Reborn-main\src\KronosReborn\MainWindow.xaml'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Remove the view toggle button section
old1 = '''                <!--
                List or poster grid.

                The grid is the poster view: one card per game with the versions it runs across the
                bottom. It is the faster way to see a library at a glance — which games are behind and
                which are current — where the list is better for comparing versions between games.
                -->
                <Button Content="{Binding ViewModeText}"
                        Style="{StaticResource ToolbarButtonStyle}"
                        Command="{Binding ToggleViewCommand}"
                        ToolTip="Switch between the list and the poster grid"
                        Margin="4,0,0,0" />

                <!--
                Search'''
new1 = '''                <!--
                Search'''

content = content.replace(old1, new1)

# 2. Remove PosterGrid checkbox
old2 = '''                <CheckBox Content="Group by library"
                          IsChecked="{Binding GroupByLibrary, Mode=TwoWay}"
                          Margin="0,0,0,8" />

                <CheckBox Content="Poster grid view"
                          IsChecked="{Binding PosterGrid, Mode=TwoWay}"
                          Margin="0,0,0,8" />

                <Button Content="Close"'''
new2 = '''                <CheckBox Content="Group by library"
                          IsChecked="{Binding GroupByLibrary, Mode=TwoWay}"
                          Margin="0,0,0,8" />

                <Button Content="Close"'''

content = content.replace(old2, new2)

# 3. Remove Grid wrapper around ListView and Visibility binding
old3 = '''            <Grid>
            <!--
            The list and the poster grid are both here and one is hidden, rather than one of them being
            rebuilt when the view changes. Rebuilding a GridView on every toggle throws away the column
            widths the user has dragged, which is a small thing that is very annoying if it happens
            every time you press the button.
            -->
            <ListView x:Name="GameListView"
                      ItemsSource="{Binding Games, Mode=OneWay}"
                      SelectedItem="{Binding SelectedGame, Mode=TwoWay}"
                      SelectionMode="Extended"
                      Visibility="{Binding IsListView, Converter={StaticResource BoolToVisibility}}"
                      VirtualizingPanel.IsVirtualizing="True"
                      VirtualizingPanel.VirtualizationMode="Recycling"
                      ScrollViewer.HorizontalScrollBarVisibility="Auto">'''
new3 = '''            <ListView x:Name="GameListView"
                      ItemsSource="{Binding Games, Mode=OneWay}"
                      SelectedItem="{Binding SelectedGame, Mode=TwoWay}"
                      SelectionMode="Extended"
                      VirtualizingPanel.IsVirtualizing="True"
                      VirtualizingPanel.VirtualizationMode="Recycling"
                      ScrollViewer.HorizontalScrollBarVisibility="Auto">'''

content = content.replace(old3, new3)

# 4. Remove the entire ListBox (poster grid) - find from comment to </ListBox>
import re
# Find and remove the poster grid ListBox section
old4_pattern = r'            <!--\s*Poster grid: the view for seeing a whole library at a glance\..*?</ListBox>\s*'
content = re.sub(old4_pattern, '', content, flags=re.DOTALL)

# 5. Remove the extra </Grid> that wrapped both views (now just need to close Border)
# After removing ListBox, we have </Grid> then </Border>. Need to remove </Grid>
old5 = '''            </ListBox>
            </Grid>
        </Border>'''
new5 = '''            </ListView>
        </Border>'''

content = content.replace(old5, new5)

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)

print("MainWindow.xaml edits done")
