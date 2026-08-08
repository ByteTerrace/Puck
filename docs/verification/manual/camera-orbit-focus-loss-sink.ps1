# Companion to camera-orbit-focus-loss.ps1 — a deliberately inert window for that harness to Alt-away TO. A
# bare WinForms Form has no context menu, so the mid-drag right-button RELEASE lands somewhere that does
# nothing at all, instead of on whatever window Alt+Tab happened to pick (a terminal would paste the clipboard
# on a right-button edge). Not runnable standalone in any useful sense — launched by its parent harness.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Puck manual harness sink'
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(1200, 500)
$form.Size = New-Object System.Drawing.Size(460, 340)
$form.BackColor = [System.Drawing.Color]::FromArgb(24, 24, 32)

[System.Windows.Forms.Application]::Run($form)
