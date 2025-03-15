namespace tja2fumen
{
    public class Tja2Fumen
    {
        static void Main()
        {

            TJASong asd =
                Parsers.ParseTja("C:\\Users\\renzo\\Documents\\TakoTako\\customSongs\\TJA\\One Last Kiss\\One Last Kiss.tja");
            FumenCourse course = Converters.ConvertTjaToFumen("Oni", asd.courses["Oni"]);
            var bytes = Writer.getFumenBytes(course);
            File.WriteAllBytes("C:\\Users\\renzo\\Documents\\TakoTako\\customSongs\\TJA\\One Last Kiss\\OLK_cs.bin", bytes);
        }
    }
}
