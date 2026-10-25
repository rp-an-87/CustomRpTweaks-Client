namespace CustomRpTweaks.Client
{
	// The thin separator line used in item tooltips: em dashes in a small, dim font
	internal static class TooltipSeparators
	{
		private const char Dash = '—';

		internal static string Line(int dashes) => "<size=7><color=#555555>" + new string(Dash, dashes) + "</color></size>";
	}
}
