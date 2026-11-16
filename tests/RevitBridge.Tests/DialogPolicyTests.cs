using RevitBridge.Tools;
using Xunit;

namespace RevitBridge.Tests;

/// <summary>How a modal dialog raised during any tool is answered (the bridge wraps every tool in DialogGuard).</summary>
public class DialogPolicyTests
{
    private const string ExportTempViewModes = "TaskDialog_Really_Print_Or_Export_Temp_View_Modes";

    [Fact]
    public void An_unknown_dialog_is_dismissed_and_OK_is_the_last_resort()
    {
        var answers = DialogPolicy.AnswersFor("TaskDialog_Delete_Elements");

        Assert.Equal(new[] { DialogPolicy.IDCANCEL, DialogPolicy.IDCLOSE, DialogPolicy.IDNO, DialogPolicy.IDOK }, answers);
    }

    [Theory]
    [InlineData(ExportTempViewModes)]
    [InlineData("taskdialog_really_print_or_export_temp_view_modes")]
    public void A_dialog_known_to_be_safe_is_confirmed(string dialogId)
        => Assert.Equal(DialogPolicy.IDOK, DialogPolicy.AnswersFor(dialogId)[0]);

    [Fact]
    public void The_first_accepted_answer_is_used_and_reported()
    {
        var offered = new List<int>();

        string report = DialogPolicy.Answer("TaskDialog_Save_Changes", answer => { offered.Add(answer); return true; });

        Assert.Equal(new[] { DialogPolicy.IDCANCEL }, offered);
        Assert.Equal("TaskDialog_Save_Changes (answered Cancel)", report);
    }

    [Fact]
    public void An_answer_the_dialog_refuses_is_followed_by_the_next_one()
    {
        var offered = new List<int>();

        // This dialog has no Cancel button (returns false) and throws for Close.
        string report = DialogPolicy.Answer("TaskDialog_Yes_No", answer =>
        {
            offered.Add(answer);
            if (answer == DialogPolicy.IDCLOSE) throw new InvalidOperationException("no such button");
            return answer == DialogPolicy.IDNO;
        });

        Assert.Equal(new[] { DialogPolicy.IDCANCEL, DialogPolicy.IDCLOSE, DialogPolicy.IDNO }, offered);
        Assert.Equal("TaskDialog_Yes_No (answered No)", report);
    }

    [Fact]
    public void A_dialog_that_refuses_every_answer_is_reported_as_such()
    {
        string report = DialogPolicy.Answer("Dialog_Stubborn", _ => false);

        Assert.Equal("Dialog_Stubborn (override rejected — dialog may need manual dismissal)", report);
    }

    [Fact]
    public void A_safe_dialog_is_reported_as_confirmed()
        => Assert.Equal($"{ExportTempViewModes} (answered OK)", DialogPolicy.Answer(ExportTempViewModes, _ => true));

    [Fact]
    public void Dismissed_dialogs_are_described_for_error_messages()
    {
        Assert.Equal(string.Empty, DialogPolicy.Describe(null));
        Assert.Equal(string.Empty, DialogPolicy.Describe(Array.Empty<string>()));
        Assert.Equal(" Auto-dismissed dialog(s) during the run: A (answered Cancel), B (answered No).",
            DialogPolicy.Describe(new[] { "A (answered Cancel)", "B (answered No)" }));
    }
}
