namespace DivisiBillWs
{

    // This is a primitive 'traits' style implementation which could be improved upon since C#8, see 
    // https://stackoverflow.com/questions/10729230/how-would-you-implement-a-trait-design-pattern-in-c
    public class StorageClass
    {
        public string TableName = "";
        public bool UseSummaryField = false;
    }

    internal class MealStorage : StorageClass
    {
        public MealStorage()
        {
            TableName = "Meal";
            UseSummaryField = true;
        }
    }
    internal class VenueListStorage : StorageClass
    {
        public VenueListStorage()
        {
            TableName = "VenueList";
        }
    }
    internal class PersonListStorage : StorageClass
    {
        public PersonListStorage()
        {
            TableName = "PersonList";
        }
    }
}
