// Targets for check_architecture: a deliberate layering violation. The Domain
// type constructs an Infrastructure type in a method body (giving the edge a
// usable location) while the Infrastructure side stays clean.

namespace Production.Domain
{
    public class OrderAggregate
    {
        public decimal Total { get; private set; }

        public void Persist()
        {
            var repository = new Production.Infrastructure.SqlOrderRepository();
            repository.Save(Total);
        }
    }
}

namespace Production.Infrastructure
{
    public class SqlOrderRepository
    {
        public void Save(decimal total)
        {
            _ = total;
        }
    }
}
