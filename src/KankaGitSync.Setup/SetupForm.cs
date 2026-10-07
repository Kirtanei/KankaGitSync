using System.Diagnostics;

namespace KankaGitSync.Setup;

internal sealed class SetupForm : Form
{
    private readonly TextBox worldDirectory = new() { Width = 420 };
    private readonly TextBox campaignId = new() { Width = 160 };
    private readonly TextBox token = new() { Width = 420, UseSystemPasswordChar = true };
    private readonly Button prepareButton = new() { Text = "Prepare world", AutoSize = true };
    private readonly Button importButton = new() { Text = "Import campaign", AutoSize = true, Enabled = false };
    private string? preparedWorldDirectory;

    public SetupForm()
    {
        Text = "Kanka Git Sync setup";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AutoScaleMode = AutoScaleMode.Dpi;
        MaximizeBox = false;
        MinimizeBox = false;
        worldDirectory.Text = CampaignSetup.DefaultWorldDirectory();
        var browseButton = new Button { Text = "Browse…", AutoSize = true };
        browseButton.Click += Browse;
        prepareButton.Click += PrepareAsync;
        importButton.Click += ImportAsync;
        Controls.Add(CreateLayout(browseButton));
        AcceptButton = prepareButton;
    }

    private Control CreateLayout(Button browseButton)
    {
        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16), ColumnCount = 3, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "World folder", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(worldDirectory, 1, 0);
        layout.Controls.Add(browseButton, 2, 0);
        layout.Controls.Add(new Label { Text = "Kanka campaign ID", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(campaignId, 1, 1);
        layout.Controls.Add(new Label { Text = "Kanka API token", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(token, 1, 2);
        layout.SetColumnSpan(token, 2);
        var explanation = new Label
        {
            Text = "The token is stored only in this world's ignored .env file. It is never added to Git.",
            AutoSize = true,
            MaximumSize = new Size(560, 0)
        };
        layout.Controls.Add(explanation, 0, 3);
        layout.SetColumnSpan(explanation, 3);
        layout.Controls.Add(prepareButton, 1, 4);
        layout.Controls.Add(importButton, 2, 4);
        return layout;
    }

    private void Browse(object? sender, EventArgs eventArgs)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = worldDirectory.Text, Description = "Choose or create a Kanka world folder" };
        if (dialog.ShowDialog(this) == DialogResult.OK) worldDirectory.Text = dialog.SelectedPath;
    }

    private async void PrepareAsync(object? sender, EventArgs eventArgs)
    {
        SetBusy(true);
        try
        {
            GitLocator.Find();
            var repository = await CampaignSetup.PrepareAsync(new SetupRequest(worldDirectory.Text, campaignId.Text, token.Text)).ConfigureAwait(true);
            preparedWorldDirectory = repository.Root;
            token.Clear();
            importButton.Enabled = true;
            MessageBox.Show(this, "Your world is ready. Review the folder, then select Import campaign when you are ready to contact Kanka.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (SyncException exception)
        {
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Setup failed ({exception.GetType().Name}). Check Git and the selected folder.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private async void ImportAsync(object? sender, EventArgs eventArgs)
    {
        if (preparedWorldDirectory == null) return;
        if (MessageBox.Show(this, "Import will read your Kanka campaign and create the initial Git history. Continue?", Text,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        SetBusy(true);
        try
        {
            var executable = FindSynchronizerExecutable();
            var start = new ProcessStartInfo(executable) { WorkingDirectory = preparedWorldDirectory, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("import");
            using var process = Process.Start(start) ?? throw new SyncException("Could not start Git Kanka import.");
            await process.WaitForExitAsync().ConfigureAwait(true);
            if (process.ExitCode != 0) throw new SyncException("Import did not complete. Open a terminal in the world folder and run git kanka import for safe diagnostics.");
            MessageBox.Show(this, "Import completed. Open a new terminal and run git kanka status in your world folder.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (SyncException exception)
        {
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { SetBusy(false); }
    }

    private static string FindSynchronizerExecutable()
    {
        foreach (var fileName in new[] { "git-kanka.exe", "KankaGitSync.exe" })
        {
            var executable = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(executable)) return executable;
        }

        throw new SyncException("Git Kanka is not installed beside the setup wizard.");
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        prepareButton.Enabled = !busy;
        importButton.Enabled = !busy && preparedWorldDirectory != null;
    }
}
