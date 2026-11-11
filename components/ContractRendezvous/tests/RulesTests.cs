using System;
using KerbalSpaceRace.ContractRendezvous;

internal static class RulesTests
{
    private static int checks;

    private static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new Exception(message);
        }

        checks++;
        Console.WriteLine("PASS " + message);
    }

    private static bool Match(
        bool sameBody = true,
        bool firstOrbiting = true,
        bool secondOrbiting = true,
        uint[] first = null,
        uint[] second = null,
        double distance = 1500.0,
        double maximum = 2000.0)
    {
        return KSRRendezvousRules.MeetsConditions(
            sameBody, firstOrbiting, secondOrbiting,
            first ?? new uint[] { 10 }, second ?? new uint[] { 20 },
            distance, maximum);
    }

    public static int Main()
    {
        try
        {
            Check(Match(), "independent craft in target orbit and range are accepted");
            Check(!Match(sameBody: false), "a vessel around Iota cannot satisfy Gael rendezvous");
            Check(!Match(firstOrbiting: false), "a suborbital primary vessel is rejected");
            Check(!Match(secondOrbiting: false), "a non-orbiting target vessel is rejected");
            Check(!Match(first: new uint[] { 10 }, second: new uint[] { 10 }),
                "separated vessels from the same launch are rejected");
            Check(!Match(first: new uint[] { 10, 20 }, second: new uint[] { 20 }),
                "overlapping docked launch lineage is rejected");
            Check(!Match(first: new uint[] { 0 }, second: new uint[] { 20 }),
                "unknown launch lineage cannot receive credit");
            Check(!Match(distance: 2000.0), "the distance boundary is exclusive");
            Check(!Match(distance: 2500.0), "craft outside range are rejected");
            Check(KSRRendezvousRules.MeetsDockingConditions(
                    true, true, true, new uint[] { 10 }, new uint[] { 20 }),
                "orbital docking between independent launches is accepted");
            Check(!KSRRendezvousRules.MeetsDockingConditions(
                    false, true, true, new uint[] { 10 }, new uint[] { 20 }),
                "docking around the wrong body is rejected");
            Check(!KSRRendezvousRules.MeetsDockingConditions(
                    true, true, true, new uint[] { 10 }, new uint[] { 10 }),
                "redocking parts from the same launch is rejected");
            Check(!KSRRendezvousRules.MeetsDockingConditions(
                    true, false, true, new uint[] { 10 }, new uint[] { 20 }),
                "non-orbital docking is rejected");
            Console.WriteLine("TOTAL PASS " + checks);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
