﻿#define DEBUG_CC2D_RAYS
using UnityEngine;
using System;
using System.Collections.Generic;


[RequireComponent( typeof( BoxCollider2D ), typeof( Rigidbody2D ) )]
public class CharacterController2D : MonoBehaviour
{
	#region internal types

	private struct CharacterRaycastOrigins
	{
		public Vector3 topRight;
		public Vector3 topLeft;
		public Vector3 bottomRight;
		public Vector3 bottomLeft;
	}

	public class CharacterCollisionState2D
	{
		public bool right;
		public bool left;
		public bool above;
		public bool below;
		public bool becameGroundedThisFrame;
		public bool movingDownSlope;
		public float slopeAngle;


		public bool hasCollision()
		{
			return below || right || left || above;
		}


		public void reset()
		{
			right = left = above = below = becameGroundedThisFrame = movingDownSlope = false;
			slopeAngle = 0f;
		}


		public override string ToString()
		{
			return string.Format( "[CharacterCollisionState2D] r: {0}, l: {1}, a: {2}, b: {3}, movingDownSlope: {4}, angle: {5}",
			                     right, left, above, below, movingDownSlope, slopeAngle );
		}
	}

	#endregion


	#region events, properties and fields

	public event Action<RaycastHit2D> onControllerCollidedEvent;
	public event Action<Collider2D> onTriggerEnterEvent;
	public event Action<Collider2D> onTriggerStayEvent;
	public event Action<Collider2D> onTriggerExitEvent;


	/// <summary>
	/// toggles if the RigidBody2D methods should be used for movement or if Transform.Translate will be used. All the usual Unity rules for physics based movement apply when true
	/// such as getting your input in Update and only calling move in FixedUpdate amonst others.
	/// </summary>
	public bool usePhysicsForMovement = false;

	[SerializeField]
	[Range( 0.001f, 0.3f )]
	private float _skinWidth = 0.02f;

	/// <summary>
	/// defines how far in from the edges of the collider rays are cast from. If cast with a 0 extent it will often result in ray hits that are
	/// not desired (for example a foot collider casting horizontally from directly on the surface can result in a hit)
	/// </summary>
	public float skinWidth
	{
		get { return _skinWidth; }
		set
		{
			_skinWidth = value;
			recalculateDistanceBetweenRays();
		}
	}


	/// <summary>
	/// mask with all layers that the player should interact with
	/// </summary>
	public LayerMask platformMask = 0;

	/// <summary>
	/// mask with all layers that should act as one-way platforms. Note that one-way platforms should always be EdgeCollider2Ds. This is private because it does not support being
	/// updated anytime outside of the inspector for now.
	/// </summary>
	[SerializeField]
	private LayerMask oneWayPlatformMask = 0;


	/// <summary>
	/// the max slope angle that the CC2D can climb
	/// </summary>
	/// <value>The slope limit.</value>
	[Range( 0, 90f )]
	public float slopeLimit = 30f;


	/// <summary>
	/// curve for multiplying speed based on slope (negative = down slope and positive = up slope)
	/// </summary>
	public AnimationCurve slopeSpeedMultiplier = new AnimationCurve( new Keyframe( -90, 1.5f ), new Keyframe( 0, 1 ), new Keyframe( 90, 0 ) );

	[Range( 2, 20 )]
	public int totalHorizontalRays = 8;
	[Range( 2, 20 )]
	public int totalVerticalRays = 4;


	/// <summary>
	/// this is used to calculate the downward ray that is cast to check for slopes. We use the somewhat arbitray value 75 degrees
	/// to calcuate the lenght of the ray that checks for slopes.
	/// </summary>
	private float _slopeLimitTangent = Mathf.Tan( 75f * Mathf.Deg2Rad );


	/// <summary>
	/// if true, a new GameObject named CC2DTriggerHelper will be created in Awake and latched on via a DistanceJoint2D
	/// to the player so that trigger messages can be received
	/// </summary>
	public bool createTriggerHelperGameObject = false;

	[Range( 0.8f, 0.999f )]
	public float triggerHelperBoxColliderScale = 0.95f;


	[HideInInspector]
	public new Transform transform;
	[HideInInspector]
	public BoxCollider2D boxCollider;
	[HideInInspector]
	public Rigidbody2D rigidBody2D;

	[HideInInspector]
	[NonSerialized]
	public CharacterCollisionState2D collisionState = new CharacterCollisionState2D();
	[HideInInspector]
	[NonSerialized]
	public Vector3 velocity;
	public bool isGrounded { get { return collisionState.below; } }

	private const float kSkinWidthFloatFudgeFactor = 0.001f;

	#endregion


	/// <summary>
	/// holder for our raycast origin corners (TR, TL, BR, BL)
	/// </summary>
	private CharacterRaycastOrigins _raycastOrigins;

	/// <summary>
	/// stores our raycast hit during movement
	/// </summary>
	private RaycastHit2D _raycastHit;

	/// <summary>
	/// stores any raycast hits that occur this frame. we have to store them in case we get a hit moving
	/// horizontally and vertically so that we can send the events after all collision state is set
	/// </summary>
	private List<RaycastHit2D> _raycastHitsThisFrame = new List<RaycastHit2D>( 2 );

	// horizontal/vertical movement data
	private float _verticalDistanceBetweenRays;
	private float _horizontalDistanceBetweenRays;


	#region Monobehaviour

	void Awake()
	{
		// add our one-way platforms to our normal platform mask so that we can land on them from above
		platformMask |= oneWayPlatformMask;

		// cache some components
		transform = GetComponent<Transform>();
		boxCollider = GetComponent<BoxCollider2D>();
		rigidBody2D = GetComponent<Rigidbody2D>();

		if( createTriggerHelperGameObject )
			createTriggerHelper();

		// here, we trigger our properties that have setters with bodies
		skinWidth = _skinWidth;
	}


	public void OnTriggerEnter2D( Collider2D col )
	{
		if( onTriggerEnterEvent != null )
			onTriggerEnterEvent( col );
	}


	public void OnTriggerStay2D( Collider2D col )
	{
		if( onTriggerStayEvent != null )
			onTriggerStayEvent( col );
	}


	public void OnTriggerExit2D( Collider2D col )
	{
		if( onTriggerExitEvent != null )
			onTriggerExitEvent( col );
	}

	#endregion


	[System.Diagnostics.Conditional( "DEBUG_CC2D_RAYS" )]
	private void DrawRay( Vector3 start, Vector3 dir, Color color )
	{
		Debug.DrawRay( start, dir, color );
	}


	#region Public

	public void move( Vector3 deltaMovement )
	{
		// save off our current grounded state
		var wasGroundedBeforeMoving = collisionState.below;
		var previousPosition = transform.position;

		// clear our state
		collisionState.reset();
		_raycastHitsThisFrame.Clear();

		var desiredPosition = transform.position + deltaMovement;
		primeRaycastOrigins( desiredPosition, deltaMovement );


		// first, we check for a slope below us before moving
		// only check slopes if we are going down and grounded
		//if( deltaMovement.y < 0 && wasGroundedBeforeMoving )
		//	handleVerticalSlope( ref deltaMovement );



//#if UNITY_4_5 || UNITY_4_6
//		boxCollider.enabled = true;
//		var isGoingRight = deltaMovement.x > 0;
//		var isGoingUp = deltaMovement.y > 0;
//
//		//Debug.Log( "no hor: " + ( deltaMovement.x == 0 ) );
//		//Debug.Log( "no ver: " + ( deltaMovement.y == 0 ) );
//
//		var skinWidthVector = new Vector3( isGoingRight ? skinWidth : -skinWidth, isGoingUp ? skinWidth : -skinWidth );
//		var posMod = Vector3.zero;
//		if( wasGroundedBeforeMoving )
//			posMod.y += skinWidth;
//
//		var scaledColliderSize = new Vector2( boxCollider.size.x * Mathf.Abs( transform.localScale.x ), boxCollider.size.y * Mathf.Abs( transform.localScale.y ) );
//		var hits = Physics2D.BoxCastAll( transform.position + posMod, scaledColliderSize, 0f, deltaMovement.normalized, deltaMovement.magnitude, platformMask );
//		if( hits.Length > 0 )
//		{
//			if( hits.Length > 1 )
//				Debug.Log( "hits: " + hits.Length );
//
//			foreach( var hit in hits )
//			{
//				//Debug.Log( "fraction: " + hit.fraction );
//				transform.position = hit.centroid;
//				collisionState.below = true;
//				//Debug.Log( "centroid: " + hit.centroid );
//			}
//		}
//		else
//		{
//			transform.Translate( deltaMovement );
//			velocity = deltaMovement / Time.deltaTime;
//		}
//		return;
//#endif





		// now we check movement in the horizontal dir
		if( deltaMovement.x != 0 )
		{
			if( wasGroundedBeforeMoving )
				transform.Translate( 0, skinWidth, 0 );

			moveHorizontally( ref deltaMovement );

			if( wasGroundedBeforeMoving )
				transform.Translate( 0, -skinWidth, 0 );
		}

		// next, check movement in the vertical dir
		if( deltaMovement.y != 0 )
			moveVertically( ref deltaMovement, wasGroundedBeforeMoving );


		// move then update our state
		if( usePhysicsForMovement )
		{
#if UNITY_4_5 || UNITY_4_6
			rigidbody2D.MovePosition( transform.position + deltaMovement );
#else
			rigidbody2D.velocity = deltaMovement / Time.fixedDeltaTime;
#endif
			velocity = rigidbody2D.velocity;
		}
		else
		{
			transform.Translate( deltaMovement );
			velocity = ( transform.position - previousPosition ) / Time.deltaTime;
		}

		// set our becameGrounded state based on the previous and current collision state
		if( !wasGroundedBeforeMoving && collisionState.below )
			collisionState.becameGroundedThisFrame = true;

		// send off the collision events if we have a listener
		if( onControllerCollidedEvent != null )
		{
			for( var i = 0; i < _raycastHitsThisFrame.Count; i++ )
				onControllerCollidedEvent( _raycastHitsThisFrame[i] );
		}
	}


	/// <summary>
	/// this should be called anytime you have to modify the BoxCollider2D at runtime. It will recalculate the distance between the rays used for collision detection.
	/// It is also used in the skinWidth setter in case it is changed at runtime.
	/// </summary>
	public void recalculateDistanceBetweenRays()
	{
		// figure out the distance between our rays in both directions
		// horizontal
		var colliderUseableHeight = boxCollider.size.y * Mathf.Abs( transform.localScale.y ) - ( 2f * _skinWidth );
		_verticalDistanceBetweenRays = colliderUseableHeight / ( totalHorizontalRays - 1 );

		// vertical
		var colliderUseableWidth = boxCollider.size.x * Mathf.Abs( transform.localScale.x ) - ( 2f * _skinWidth );
		_horizontalDistanceBetweenRays = colliderUseableWidth / ( totalVerticalRays - 1 );
	}


	/// <summary>
	/// this is called internally if createTriggerHelperGameObject is true. It is provided as a public method
	/// in case you want to grab a handle on the GO created to modify it in some way. Note that by default only
	/// collisions with triggers will be allowed to pass through and fire the events.
	/// </summary>
	public GameObject createTriggerHelper()
	{
		var go = new GameObject( "PlayerTriggerHelper" );
		go.hideFlags = HideFlags.HideInHierarchy;
		go.layer = gameObject.layer;
		// scale is slightly less so that we don't get trigger messages when colliding with non-triggers
		go.transform.localScale = transform.localScale * triggerHelperBoxColliderScale;

		go.AddComponent<CC2DTriggerHelper>().setParentCharacterController( this );

		var rb = go.AddComponent<Rigidbody2D>();
		rb.mass = 0f;
		rb.gravityScale = 0f;

		var bc = go.AddComponent<BoxCollider2D>();
		bc.size = boxCollider.size;
		bc.isTrigger = true;

		var joint = go.AddComponent<DistanceJoint2D>();
		joint.connectedBody = rigidbody2D;
		joint.distance = 0f;

		return go;
	}

	#endregion


	#region Private Movement Methods

	/// <summary>
	/// resets the raycastOrigins to the current extents of the box collider inset by the skinWidth. It is inset
	/// to avoid casting a ray from a position directly touching another collider which results in wonky normal data.
	/// </summary>
	/// <param name="futurePosition">Future position.</param>
	/// <param name="deltaMovement">Delta movement.</param>
	private void primeRaycastOrigins( Vector3 futurePosition, Vector3 deltaMovement )
	{
		var scaledColliderSize = new Vector2( boxCollider.size.x * Mathf.Abs( transform.localScale.x ), boxCollider.size.y * Mathf.Abs( transform.localScale.y ) ) / 2;
		var scaledCenter = new Vector2( boxCollider.center.x * transform.localScale.x, boxCollider.center.y * transform.localScale.y );

		_raycastOrigins.topRight = transform.position + new Vector3( scaledCenter.x + scaledColliderSize.x, scaledCenter.y + scaledColliderSize.y );
		_raycastOrigins.topRight.x -= _skinWidth;
		_raycastOrigins.topRight.y -= _skinWidth;

		_raycastOrigins.topLeft = transform.position + new Vector3( scaledCenter.x - scaledColliderSize.x, scaledCenter.y + scaledColliderSize.y );
		_raycastOrigins.topLeft.x += _skinWidth;
		_raycastOrigins.topLeft.y -= _skinWidth;

		_raycastOrigins.bottomRight = transform.position + new Vector3( scaledCenter.x + scaledColliderSize.x, scaledCenter.y -scaledColliderSize.y );
		_raycastOrigins.bottomRight.x -= _skinWidth;
		_raycastOrigins.bottomRight.y += _skinWidth;

		_raycastOrigins.bottomLeft = transform.position + new Vector3( scaledCenter.x - scaledColliderSize.x, scaledCenter.y -scaledColliderSize.y );
		_raycastOrigins.bottomLeft.x += _skinWidth;
		_raycastOrigins.bottomLeft.y += _skinWidth;
	}


	/// <summary>
	/// we have to use a bit of trickery in this one. The rays must be cast from a small distance inside of our
	/// collider (skinWidth) to avoid zero distance rays which will get the wrong normal. Because of this small offset
	/// we have to increase the ray distance skinWidth then remember to remove skinWidth from deltaMovement before
	/// actually moving the player
	/// </summary>
	private void moveHorizontally( ref Vector3 deltaMovement )
	{
		var isGoingRight = deltaMovement.x > 0;
		var rayDistance = Mathf.Abs( deltaMovement.x ) + _skinWidth;
		var rayDirection = isGoingRight ? Vector2.right : -Vector2.right;
		var posMod = isGoingRight ? new Vector3( -skinWidth, 0, 0 ) : new Vector3( skinWidth, 0, 0 );

		var scaledColliderSize = new Vector2( boxCollider.size.x * Mathf.Abs( transform.localScale.x ), boxCollider.size.y * Mathf.Abs( transform.localScale.y ) );
		var hits = Physics2D.BoxCastAll( transform.position + posMod, scaledColliderSize, 0f, rayDirection, Mathf.Abs( deltaMovement.x ) + skinWidth, platformMask );
		if( hits.Length > 0 )
		{
			if( hits.Length > 1 )
				Debug.Log( "hits: " + hits.Length );

			foreach( var hit in hits )
			{
				//Debug.Log( "fraction: " + hit.fraction );
				transform.position = hit.centroid;

				if( isGoingRight )
					collisionState.right = true;
				else
					collisionState.left = true;
				//Debug.Log( "centroid: " + hit.centroid );
			}
		}
		else
		{
			transform.Translate( deltaMovement.x, 0, 0 );
		}

		deltaMovement.x = 0;
	}


	private bool handleHorizontalSlope( ref Vector3 deltaMovement, float angle, bool isGoingRight )
	{
		// disregard 90 degree angles (walls)
		if( Mathf.RoundToInt( angle ) == 90 )
			return false;

		// if we can walk on slopes and our angle is small enough we need to move up
		if( angle < slopeLimit )
		{
			// we only need to adjust the y movement if we are not jumping
			// TODO: this uses a magic number which isn't ideal!
			if( deltaMovement.y < 0.07f )
			{
				// apply the slopeModifier to slow our movement up the slope
				var slopeModifier = slopeSpeedMultiplier.Evaluate( angle );
				deltaMovement.x *= slopeModifier;

				// we dont set collisions on the sides for this since a slope is not technically a side collision
				if( isGoingRight )
				{
					deltaMovement.x -= _skinWidth;
				}
				else
				{
					deltaMovement.x += _skinWidth;
				}

				// smooth y movement when we climb. we make the y movement equivalent to the actual y location that corresponds
				// to our new x location using our good friend Pythagoras
				deltaMovement.y = Mathf.Abs( Mathf.Tan( angle * Mathf.Deg2Rad ) * deltaMovement.x );

				collisionState.below = true;
			}
		}
		else // too steep. get out of here
		{
			deltaMovement.x = 0;
		}

		return true;
	}


	private void moveVertically( ref Vector3 deltaMovement, bool wasGroundedBeforeMoving )
	{
		var isGoingUp = deltaMovement.y > 0;
		var rayDistance = Mathf.Abs( deltaMovement.y );
		var rayDirection = isGoingUp ? Vector2.up : -Vector2.up;
		var posMod = wasGroundedBeforeMoving && isGoingUp ? new Vector3( 0, skinWidth, 0 ) : Vector3.zero;

		// if we are moving up, we should ignore the layers in oneWayPlatformMask
		var mask = platformMask;
		if( isGoingUp )
			mask &= ~oneWayPlatformMask;


		var scaledColliderSize = new Vector2( boxCollider.size.x * Mathf.Abs( transform.localScale.x ), boxCollider.size.y * Mathf.Abs( transform.localScale.y ) );
		var hits = Physics2D.BoxCastAll( transform.position + posMod, scaledColliderSize, 0f, rayDirection, Mathf.Abs( deltaMovement.y ), mask );
		if( hits.Length > 0 )
		{
			if( hits.Length > 1 )
				Debug.Log( "hits: " + hits.Length );

			foreach( var hit in hits )
			{
				//Debug.Log( "fraction: " + hit.fraction );
				transform.position = hit.centroid;

				if( isGoingUp )
					collisionState.above = true;
				else
					collisionState.below = true;
				//Debug.Log( "centroid: " + hit.centroid );
			}
		}
		else
		{
			transform.Translate( 0, deltaMovement.y, 0 );
		}

		deltaMovement.y = 0;
	}


	/// <summary>
	/// checks the center point under the BoxCollider2D for a slope. If it finds one then the deltaMovement is adjusted so that
	/// the player stays grounded and the slopeSpeedModifier is taken into account to speed up movement.
	/// </summary>
	/// <param name="deltaMovement">Delta movement.</param>
	private void handleVerticalSlope( ref Vector3 deltaMovement )
	{
		// slope check from the center of our collider
		var centerOfCollider = ( _raycastOrigins.bottomLeft.x + _raycastOrigins.bottomRight.x ) * 0.5f;
		var rayDirection = -Vector2.up;

		// the ray distance is based on our slopeLimit
		var slopeCheckRayDistance = _slopeLimitTangent * ( _raycastOrigins.bottomRight.x - centerOfCollider );

		var slopeRay = new Vector2( centerOfCollider, _raycastOrigins.bottomLeft.y );
		DrawRay( slopeRay, rayDirection * slopeCheckRayDistance, Color.yellow );
		_raycastHit = Physics2D.Raycast( slopeRay, rayDirection, slopeCheckRayDistance, platformMask );
		if( _raycastHit )
		{
			// bail out if we have no slope
			var angle = Vector2.Angle( _raycastHit.normal, Vector2.up );
			if( angle == 0 )
				return;

			// we are moving down the slope if our normal and movement direction are in the same x direction
			var isMovingDownSlope = Mathf.Sign( _raycastHit.normal.x ) == Mathf.Sign( deltaMovement.x );
			if( isMovingDownSlope )
			{
				// going down we want to speed up in most cases so the slopeSpeedMultiplier curve should be > 1 for negative angles
				var slopeModifier = slopeSpeedMultiplier.Evaluate( -angle );
				deltaMovement.y = _raycastHit.point.y - slopeRay.y;
				deltaMovement.x *= slopeModifier;
				collisionState.movingDownSlope = true;
				collisionState.slopeAngle = angle;
			}
		}
	}

	#endregion


}
