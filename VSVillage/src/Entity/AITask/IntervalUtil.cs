using Vintagestory.API.Common;

namespace VsVillage;

public class IntervalUtil
{
	public static bool matchesCurrentTime(DayTimeFrame[] dayTimeFrames, IWorldAccessor world, float offset = 0f)
	{
		bool flag = false;
		if (dayTimeFrames != null)
		{
			float num = world.Calendar.HourOfDay / world.Calendar.HoursPerDay * 24f;
			int num2 = 0;
			while (!flag && num2 < dayTimeFrames.Length)
			{
				flag |= dayTimeFrames[num2].Matches(num + offset);
				num2++;
			}
		}
		return flag;
	}
}
