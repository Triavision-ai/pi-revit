import { Type } from "typebox";
import { StringEnum } from "@earendil-works/pi-ai";

/**
 * A tool parameter that accepts one of a fixed list of words, declared as the plain
 * { type: "string", enum: [...] } schema Pi recommends because every model provider accepts
 * it. A union of literals becomes anyOf/const instead, which some providers reject.
 *
 * Declare every string choice of a Pi-native tool through these two functions.
 */
export function stringChoice<T extends readonly string[]>(values: T, options?: { description?: string }) {
	return StringEnum(values, options);
}

/**
 * The optional form. Type.Unsafe makes TypeBox treat StringEnum's plain object as one of its own
 * schemas. Applied directly to that plain object, Type.Optional leaves its internal "~optional"
 * marker visible in the JSON schema, and that schema is what the model provider receives.
 */
export function optionalStringChoice<T extends readonly string[]>(values: T, options?: { description?: string }) {
	return Type.Optional(Type.Unsafe<T[number]>(StringEnum(values, options)));
}
