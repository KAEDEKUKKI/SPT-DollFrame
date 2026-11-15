using System;

namespace GF2.Tests
{
    public static class VanillaOptionTest
    {
        public static int Run()
        {
            var pkgs = new[] { "Koleda", "Groza" };
            var list = VanillaSelection.PrependVanilla(pkgs);
            if (list.Length != 3)
            {
                Console.Error.WriteLine($"vanilla: expected 3 items, got {list.Length}");
                return 1;
            }
            if (list[0] != VanillaSelection.VanillaPackageName)
            {
                Console.Error.WriteLine($"vanilla: expected index 0 to be {VanillaSelection.VanillaPackageName}, got {list[0]}");
                return 1;
            }
            if (!VanillaSelection.IsVanilla(VanillaSelection.VanillaPackageName) ||
                !VanillaSelection.IsVanilla("") ||
                !VanillaSelection.IsVanilla(null))
            {
                Console.Error.WriteLine("vanilla: IsVanilla check failed on vanilla names");
                return 1;
            }
            if (VanillaSelection.IsVanilla("Koleda"))
            {
                Console.Error.WriteLine("vanilla: IsVanilla check failed on character name");
                return 1;
            }

            Console.WriteLine("vanilla option: OK");
            return 0;
        }
    }
}
