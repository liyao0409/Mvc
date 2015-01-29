﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.AspNet.Mvc.ModelBinding.Internal;

namespace Microsoft.AspNet.Mvc.ModelBinding
{
    /// <summary>
    /// Recursively validate an object.
    /// </summary>
    public class DefaultModelValidator
    {
        /// <inheritdoc />
        public bool Validate([NotNull] ModelValidationContext modelValidationContext, string keyPrefix)
        {
            var metadata = modelValidationContext.ModelMetadata;
            var validationContext = new ValidationContext()
            {
                ModelValidationContext = modelValidationContext,
                Visited = new HashSet<object>(ReferenceEqualityComparer.Instance),
            };

            return ValidateNonVisitedNodeAndChildren(keyPrefix, metadata, validationContext, validators: null);
        }

        private bool ValidateNonVisitedNodeAndChildren(string modelKey,
			ModelMetadata metadata, ValidationContext validationContext, IEnumerable<IModelValidator> validators)
        {
            // Recursion guard to avoid stack overflows
            RuntimeHelpers.EnsureSufficientExecutionStack();

            var isValid = true;
            if (validators == null)
            {
                // The validators are not null in the case of validating an array. Since the validators are
                // the same for all the elements of the array, we do not do GetValidators for each element,
                // instead we just pass them over. See ValidateElements function.
                validators = validationContext.ModelValidationContext.ValidatorProvider.GetValidators(metadata);
            }

            var modelState = validationContext.ModelValidationContext.ModelState;
            // We don't need to recursively traverse the graph for null values
            if (metadata.Model == null)
            {
                return ShallowValidate(modelKey, metadata, validationContext, validators);
            }

            // We don't need to recursively traverse the graph for types that shouldn't be validated
            var modelType = metadata.Model.GetType();
            if (IsTypeExcludedFromValidation(
                validationContext.ModelValidationContext.ExcludeFromValidationFilters,
                modelType))
            {
                return ShallowValidate(modelKey, metadata, validationContext, validators);
            }

            // Check to avoid infinite recursion. This can happen with cycles in an object graph.
            if (validationContext.Visited.Contains(metadata.Model))
            {
                return true;
            }

            validationContext.Visited.Add(metadata.Model);

            // Validate the children first - depth-first traversal
            var enumerableModel = metadata.Model as IEnumerable;
            if (enumerableModel == null)
            {
                isValid = ValidateProperties(modelKey, metadata, validationContext);
            }
            else
            {
                isValid = ValidateElements(modelKey, enumerableModel, validationContext);
            }

            if (isValid)
            {
                // Don't bother to validate this node if children failed.
                isValid = ShallowValidate(modelKey, metadata, validationContext, validators);
            }

            // Pop the object so that it can be validated again in a different path
            validationContext.Visited.Remove(metadata.Model);
            return isValid;
        }

        private bool ValidateProperties(string currentModelKey, ModelMetadata metadata, ValidationContext validationContext)
        {
            var isValid = true;

            foreach (var childMetadata in
                validationContext.ModelValidationContext.MetadataProvider.GetMetadataForProperties(
                    metadata.Model, metadata.RealModelType))
            {
                var childKey = ModelBindingHelper.CreatePropertyModelName(currentModelKey, childMetadata.PropertyName);
                if (!ValidateNonVisitedNodeAndChildren(childKey, childMetadata, validationContext, validators: null))
                {
                    isValid = false;
                }
            }

            return isValid;
        }

        private bool ValidateElements(string currentKey, IEnumerable model, ValidationContext validationContext)
        {
            var elementType = GetElementType(model.GetType());
            var elementMetadata =
                validationContext.ModelValidationContext.MetadataProvider.GetMetadataForType(
                    modelAccessor: null, modelType: elementType);

            var validators = validationContext.ModelValidationContext.ValidatorProvider.GetValidators(elementMetadata);

            // If there are no validators or the object is null we bail out quickly
            // when there are large arrays of null, this will save a significant amount of processing
            // with minimal impact to other scenarios.
            var anyValidatorsDefined = validators.Any();
			var index = 0;
            var isValid = true;
			foreach (var element in model)
            {
                // If the element is non null, the recursive calls might find more validators.
                // If it's null, then a shallow validation will be performed.
                if (element != null || anyValidatorsDefined)
                {
                    elementMetadata.Model = element;
					var elementKey = ModelBindingHelper.CreateIndexModelName(currentKey, index);
					if (!ValidateNonVisitedNodeAndChildren(elementKey, elementMetadata, validationContext, validators))
                    {
                        isValid = false;
                    }
                }

                index++;
            }

            return isValid;
        }

        // Validates a single node (not including children)
        // Returns true if validation passes successfully
        private static bool ShallowValidate(
            string modelKey,
            ModelMetadata metadata,
            ValidationContext validationContext,
            [NotNull] IEnumerable<IModelValidator> validators)
        {
            var isValid = true;

            // When the are no validators we bail quickly. This saves a GetEnumerator allocation.
            // In a large array (tens of thousands or more) scenario it's very significant.
            var validatorsAsCollection = validators as ICollection;
            if (validatorsAsCollection == null || validatorsAsCollection.Count > 0)
            {
                var modelValidationContext =
                        new ModelValidationContext(validationContext.ModelValidationContext, metadata);
                var modelState = validationContext.ModelValidationContext.ModelState;
                var fieldValidationState = modelState.GetFieldValidationState(modelKey);
				var modelValidationState = modelState.GetValidationState(modelKey);

				// If either the model or its properties are unvalidated, validate them now.
				if (modelValidationState == ModelValidationState.Unvalidated ||
					fieldValidationState == ModelValidationState.Unvalidated)
                {
                    foreach (var validator in validators)
                    {
                        foreach (var error in validator.Validate(modelValidationContext))
                        {
                            var errorKey = ModelBindingHelper.CreatePropertyModelName(modelKey, error.MemberName);
                            modelState.AddModelError(errorKey, error.Message);
                            isValid = false;
                        }
                    }
                }
                else if (fieldValidationState == ModelValidationState.Invalid)
                {
                    isValid = false;
                }
            }

            if (isValid)
            {
                validationContext.ModelValidationContext.ModelState.MarkFieldValid(modelKey);
            }

            return isValid;
        }

        private bool IsTypeExcludedFromValidation(IReadOnlyList<IExcludeTypeValidationFilter> filters, Type type)
        {
            // This can be set to null in ModelBinding scenarios which does not flow through this path.
            if (filters == null)
            {
                return false;
            }

            return filters.Any(filter => filter.IsTypeExcluded(type));
        }

        private static Type GetElementType(Type type)
        {
            Debug.Assert(typeof(IEnumerable).IsAssignableFrom(type));
            if (type.IsArray)
            {
                return type.GetElementType();
            }

            foreach (var implementedInterface in type.GetInterfaces())
            {
                if (implementedInterface.IsGenericType() &&
                    implementedInterface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return implementedInterface.GetGenericArguments()[0];
                }
            }

            return typeof(object);
        }

        private class ValidationContext
        {
            public ModelValidationContext ModelValidationContext { get; set; }

			public HashSet<object> Visited { get; set; }
        }
    }
}