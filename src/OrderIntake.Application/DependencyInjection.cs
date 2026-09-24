using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using OrderIntake.Application.Abstractions;
using OrderIntake.Application.Orders;
using OrderIntake.Application.Orders.Contracts;
using OrderIntake.Application.Orders.Validation;

namespace OrderIntake.Application;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the use-case layer.
    ///
    /// Validators are registered explicitly rather than by assembly scanning:
    /// two lines of code instead of a package reference and a reflection pass,
    /// and a missing registration fails at startup instead of at request time.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();

        services.AddSingleton<IValidator<SubmitOrderRequest>, SubmitOrderRequestValidator>();
        services.AddSingleton<IValidator<ChangeOrderStatusRequest>, ChangeOrderStatusRequestValidator>();

        services.AddScoped<IOrderService, OrderService>();

        return services;
    }
}
