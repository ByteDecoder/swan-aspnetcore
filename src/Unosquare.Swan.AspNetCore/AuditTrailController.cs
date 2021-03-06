﻿using Microsoft.EntityFrameworkCore;
using Swan.AspNetCore.Models;
using Swan.Formatters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Swan.AspNetCore
{
    /// <inheritdoc />
    /// <summary>
    /// Represents an AuditTrail controller to use with BusinessDbContext.
    /// </summary>
    /// <typeparam name="TDbContext">The type of database.</typeparam>
    /// <typeparam name="TEntity">The type of the entity.</typeparam>
    public class AuditTrailController<TDbContext, TEntity> : BusinessRulesController<TDbContext>
        where TDbContext : DbContext
    {
        private readonly List<Type> _validCreateTypes = new List<Type>();
        private readonly List<Type> _validUpdateTypes = new List<Type>();
        private readonly List<Type> _validDeleteTypes = new List<Type>();

        /// <summary>
        /// Initializes a new instance of the <see cref="AuditTrailController{T, TEntity}"/> class.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="currentUserId">The current user identifier.</param>
        public AuditTrailController(TDbContext context, string currentUserId)
            : base(context)
        {
            CurrentUserId = currentUserId;
        }

        /// <summary>
        /// Gets the current user identifier.
        /// </summary>
        /// <value>
        /// The current user identifier.
        /// </value>
        public string CurrentUserId { get; }

        /// <summary>
        /// Adds the types.
        /// </summary>
        /// <param name="action">The action.</param>
        /// <param name="types">The types.</param>
        public void AddTypes(ActionFlags action, Type[] types)
        {
            switch (action)
            {
                case ActionFlags.None:
                    break;
                case ActionFlags.Create:
                    _validCreateTypes.AddRange(types);
                    break;
                case ActionFlags.Update:
                    _validUpdateTypes.AddRange(types);
                    break;
                case ActionFlags.Delete:
                    _validDeleteTypes.AddRange(types);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }

        /// <summary>
        /// Called when [entity created].
        /// </summary>
        /// <param name="entity">The entity.</param>
        [BusinessRule(ActionFlags.Create)]
        public virtual void OnEntityCreated(object entity)
        {
            var entityType = entity.GetType();
            if (_validCreateTypes.Contains(entityType) == false && _validCreateTypes.Any()) return;

            AuditEntry(ActionFlags.Create, entity, entityType.Name);
        }

        /// <summary>
        /// Called when [entity updated].
        /// </summary>
        /// <param name="entity">The entity.</param>
        [BusinessRule(ActionFlags.Update)]
        public virtual void OnEntityUpdated(object entity)
        {
            var entityType = entity.GetType();
            if (_validUpdateTypes.Contains(entityType) == false && _validUpdateTypes.Any()) return;

            AuditEntry(ActionFlags.Update, entity, entityType.Name);
        }

        /// <summary>
        /// Called when [delete created].
        /// </summary>
        /// <param name="entity">The entity.</param>
        [BusinessRule(ActionFlags.Delete)]
        public virtual void OnDeleteCreated(object entity)
        {
            var entityType = entity.GetType();
            if (_validDeleteTypes.Contains(entityType) == false && _validDeleteTypes.Any()) return;

            AuditEntry(ActionFlags.Delete, entity, entityType.Name);
        }

        /// <summary>
        /// Audits the entry.
        /// </summary>
        /// <param name="flag">The flag.</param>
        /// <param name="entity">The entity.</param>
        /// <param name="name">The name.</param>
        protected virtual void AuditEntry(ActionFlags flag, object entity, string name)
        {
            if (string.IsNullOrWhiteSpace(CurrentUserId)) return;

            var instance = (IAuditTrailEntry)Activator.CreateInstance<TEntity>();
            instance.TableName = name;
            instance.DateCreated = DateTime.UtcNow;
            instance.Action = (int)flag;
            instance.UserId = CurrentUserId;
            instance.JsonBody = Json.Serialize(entity);

            Context.Entry(instance).State = EntityState.Added;
        }
    }
}
