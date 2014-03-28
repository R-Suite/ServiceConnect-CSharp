using System;
using R.MessageBus.Interfaces;

namespace McDonalds.Cashier
{
    public class MealData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public bool BurgerCooked { get; set; }
        public bool FoodPrepped { get; set; }
    }
}