import sys

path = r'C:\Users\Yeti\Downloads\Kronos-Reborn-main\Kronos-Reborn-main\src\KronosReborn\UI\ViewModels.cs'
with open(path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

# Remove lines 411-428 (1-indexed), which are:
# Line 411:     private System.ComponentModel.ICollectionView? ListView { get; set; }
# Line 412: (empty)
# Lines 413-428: PosterView property + AttachViews method
# Keep line 410 (empty) and line 429 (empty), remove 411-428

# Actually we want to keep line 410 and 429. Let's remove indices 410-427 (0-indexed)
del lines[410:428]

with open(path, 'w', encoding='utf-8') as f:
    f.writelines(lines)

print("Removed ListView/PosterView properties and AttachViews method")
